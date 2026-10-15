using System.Collections.Generic;
using System.Linq;
using Editor.Core.Claude;
using Editor.Core.Services;

namespace VortexTests
{
    /// <summary>#457: read_console's cursor — every Console line gets an id that only grows (also across Clear and the line
    /// cap), and since_id returns exactly the lines added since, oldest first, with next_id to continue from; removed lines
    /// count as missed only when the filter would have matched them, and a cursor from an earlier editor session starts over.</summary>
    public static class ConsoleCursorTests
    {
        private static List<LogEntry> Lines(long first, int count) =>
            Enumerable.Range(0, count).Select(i => new LogEntry { Id = first + i, Level = i % 3 == 2 ? LogLevel.Error : LogLevel.Info, Message = "line " + (first + i) }).ToList();

        [Test]
        public static void SinceIdReturnsOnlyWhatIsNew(TestContext t)
        {
            var console = ConsoleService.Instance;
            long before = console.LastId;
            console.Log("[cursor] one");
            console.LogWarning("[cursor] two");
            t.Equal(before + 2, console.LastId, "every line takes the next id");
            t.Equal(console.LastId, console.Entries[console.Entries.Count - 1].Id, "the newest line has the last id");

            var first = ConsoleCursor.Read(console.Entries, console.Removed, console.LastId, before + 1, null, 50);
            t.Equal(2, first.Lines.Count, "the two lines since the cursor");
            t.Equal("[cursor] one", first.Lines[0].Message, "oldest first");
            t.Equal(console.LastId + 1, first.NextId, "next_id: the first line not read");
            var nothing = ConsoleCursor.Read(console.Entries, console.Removed, console.LastId, first.NextId, null, 50);
            t.Equal(0, nothing.Lines.Count, "nothing new — nothing again");
            console.LogError("[cursor] three");
            var next = ConsoleCursor.Read(console.Entries, console.Removed, console.LastId, first.NextId, null, 50);
            t.Equal(1, next.Lines.Count, "only the line added since");
            t.Equal("[cursor] three", next.Lines[0].Message, "the new one");

            console.Log("[cursor] unread info");
            console.LogError("[cursor] unread error");
            console.Clear();
            t.Equal(0, console.Entries.Count, "cleared");
            console.Log("[cursor] after clear");
            t.Equal(next.NextId + 2, console.Entries[0].Id, "ids go on after Clear — a cursor never sees an old id again");
            var after = ConsoleCursor.Read(console.Entries, console.Removed, console.LastId, next.NextId, null, 50);
            t.Equal(1, after.Lines.Count, "the line after the clear");
            t.Equal(2L, after.Missed, "the two lines cleared before they were read are missed");
            var errorsOnly = ConsoleCursor.Read(console.Entries, console.Removed, console.LastId, next.NextId, e => e.Level == LogLevel.Error, 50);
            t.Equal(1L, errorsOnly.Missed, "with an error filter only the cleared error counts (#457 review)");
            t.Equal(0L, errorsOnly.MissedUnchecked, "both were remembered");
        }

        [Test]
        public static void ALimitedPageContinuesWhereItStopped(TestContext t)
        {
            var lines = Lines(101, 20);   // ids 101..120 — the first 100 went with the line cap
            var page = ConsoleCursor.Read(lines, null, 120, 95, null, 8);
            t.Equal(6L, page.Missed, "ids 95..100 were removed before they were read");
            t.Equal(8, page.Lines.Count, "the limit");
            t.Equal(101L, page.Lines[0].Id, "from the oldest line still there");
            t.Equal(12, page.More, "twelve left");
            t.Equal(109L, page.NextId, "the next page starts after the last line returned");
            var rest = ConsoleCursor.Read(lines, null, 120, page.NextId, null, 50);
            t.Equal(12, rest.Lines.Count, "the rest, none twice");
            t.Equal(121L, rest.NextId, "then the end");

            // a filter: only errors (every third line), the cursor still moves past what was looked at
            var errors = ConsoleCursor.Read(lines, null, 120, 101, e => e.Level == LogLevel.Error, 3);
            t.True(errors.Lines.All(e => e.Level == LogLevel.Error), "errors only");
            t.Equal(3, errors.Lines.Count, "three of them");
            t.Equal(errors.Lines[2].Id + 1, errors.NextId, "continues after the third error");
            t.Equal(errors.Total - 3, errors.More, "the other errors are left");

            // without since_id: the newest lines, as read_console always answered
            var newest = ConsoleCursor.Read(lines, null, 120, null, null, 5);
            t.Equal(116L, newest.Lines[0].Id, "the newest five");
            t.Equal(121L, newest.NextId, "next_id to start a cursor from");
            t.Equal(20, newest.Total, "all twenty matched");

            // a cursor from before an editor restart (ids began again) reads from the oldest line
            var restarted = ConsoleCursor.Read(lines, null, 120, 5000, null, 50);
            t.True(restarted.Restarted, "said so");
            t.Equal(20, restarted.Lines.Count, "everything there is");
        }

        [Test]
        public static void MissedCountsOnlyWhatTheFilterMatches(TestContext t)
        {
            // the reviewer's case: 5000 lines kept (1001..6000), the 1000 before removed by the cap — every 100th an error
            var removedAll = Enumerable.Range(1, 1000).Select(i => new LogEntry { Id = i, Level = i % 100 == 0 ? LogLevel.Error : LogLevel.Info, Message = "l" + i }).ToList();
            var kept = Enumerable.Range(1001, 5000).Select(i => new LogEntry { Id = i, Level = i % 100 == 0 ? LogLevel.Error : LogLevel.Info, Message = "l" + i }).ToList();
            var page = ConsoleCursor.Read(kept, removedAll, 6000, 10, e => e.Level == LogLevel.Error, 5);
            t.Equal(10L, page.Missed, "ten errors (100 … 1000) were removed unread — not the 991 lines of every level");
            t.Equal(0L, page.MissedUnchecked, "all of them remembered");
            t.Equal(991L, ConsoleCursor.Read(kept, removedAll, 6000, 10, null, 5).Missed, "without a filter every removed line is missed");
            var text = ConsoleCursor.Read(kept, removedAll, 6000, 10, e => e.Message.EndsWith("00"), 5);
            t.Equal(10L, text.Missed, "a text filter is checked against the remembered lines too");

            // only the newest removed lines are remembered: what came before them is unchecked, not guessed
            var remembered = removedAll.Skip(600).ToList();   // ids 601..1000
            var partly = ConsoleCursor.Read(kept, remembered, 6000, 10, e => e.Level == LogLevel.Error, 5);
            t.Equal(4L, partly.Missed, "700, 800, 900, 1000");
            t.Equal(591L, partly.MissedUnchecked, "ids 10 … 600 are gone without a trace");
        }
    }
}
