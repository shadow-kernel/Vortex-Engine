using System;
using System.Collections.Generic;
using Editor.Core.Services;

namespace Editor.Core.Claude
{
    /// <summary>
    /// read_console's cursor (#457): every Console line has an id that only grows (<see cref="LogEntry.Id"/>), and an
    /// answer carries <see cref="Page.NextId"/> — passed back as since_id, the next call returns only the lines added
    /// since, oldest first, so a tool that polls the log during a play test never diffs timestamps (which broke around
    /// midnight) or reads a line twice. Lines that were gone before they were read — the Console was cleared, or a chatty
    /// game pushed them past the 5000-line cap — are counted in <see cref="Page.Missed"/> instead of silently skipped.
    /// </summary>
    public static class ConsoleCursor
    {
        public sealed class Page
        {
            /// <summary>The lines to show, oldest first.</summary>
            public List<LogEntry> Lines = new List<LogEntry>();
            /// <summary>How many lines matched (without the limit).</summary>
            public int Total;
            /// <summary>The since_id of the next call: the first line not read yet.</summary>
            public long NextId;
            /// <summary>Matching lines after the ones returned (the limit cut them; the next call returns them).</summary>
            public int More;
            /// <summary>Lines from since_id on that were removed before they could be read.</summary>
            public long Missed;
            /// <summary>since_id lies beyond the newest line — the editor was restarted and its ids began again — so the page
            /// starts at the oldest line instead.</summary>
            public bool Restarted;
        }

        /// <param name="entries">The Console's lines, oldest first (ids rising).</param>
        /// <param name="lastId">The id of the newest line ever added (<see cref="ConsoleService.LastId"/>).</param>
        /// <param name="sinceId">Null: the newest <paramref name="limit"/> matching lines, as read_console always answered.
        /// Otherwise the matching lines with an id from this one on — the oldest <paramref name="limit"/> of them.</param>
        /// <param name="match">The severity / text filter (null = every line).</param>
        public static Page Read(IList<LogEntry> entries, long lastId, long? sinceId, Func<LogEntry, bool> match, int limit)
        {
            var page = new Page { NextId = lastId + 1 };
            limit = Math.Max(1, limit);
            int count = entries?.Count ?? 0;
            if (sinceId == null)
            {
                var all = new List<LogEntry>();
                for (int i = 0; i < count; i++) if (match == null || match(entries[i])) all.Add(entries[i]);
                page.Total = all.Count;
                page.Lines = all.GetRange(Math.Max(0, all.Count - limit), Math.Min(limit, all.Count));
                return page;
            }
            long since = Math.Max(1, sinceId.Value);
            if (since > lastId + 1) { page.Restarted = true; since = 1; }
            long first = count > 0 ? entries[0].Id : lastId + 1;
            if (first > since) page.Missed = first - since;
            // the first line at or after since: ids rise, so a binary search
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (entries[mid].Id < since) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < count; i++)
            {
                var e = entries[i];
                if (match != null && !match(e)) continue;
                page.Total++;
                if (page.Lines.Count < limit) page.Lines.Add(e);
            }
            page.More = page.Total - page.Lines.Count;
            if (page.More > 0) page.NextId = page.Lines[page.Lines.Count - 1].Id + 1;   // continue right after the last line returned
            return page;
        }
    }
}
