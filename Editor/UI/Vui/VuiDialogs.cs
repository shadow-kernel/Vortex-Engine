using System;
using System.Collections.Generic;

namespace Editor.UI.Vui
{
    /// <summary>
    /// One-call modal dialogs (#45) built on the existing screen stack: Gui.Confirm pushes a
    /// programmatically-built canvas (dim + centered card + Yes/No), VuiStack routes its pseudo
    /// click actions back HERE instead of to a script actions class, and the stored callbacks fire.
    /// Gamepad-navigable for free (#44: the buttons are ordinary focusables on the top screen).
    /// </summary>
    public static class VuiDialogs
    {
        /// <summary>The screen name VuiStack intercepts (never reaches ScriptRuntime's action routing).</summary>
        public const string DialogScreenName = "__ConfirmDialog";

        private static Action _onYes, _onNo;
        private static VuiCanvas _dialog;

        /// <summary>Show a modal yes/no confirmation. A second call replaces the first (last one wins).
        /// The dialog blocks input + gameplay and frees the cursor; Yes/No pop it and invoke the callback.</summary>
        public static void Confirm(string title, string message, Action onYes, Action onNo)
        {
            Close();   // replace any pending dialog

            _onYes = onYes; _onNo = onNo;

            var root = new VuiElement
            {
                Kind = VuiKind.Panel,
                Bg = new[] { 0.02f, 0.02f, 0.03f, 0.55f },   // dim the game behind the card
                BlocksInput = true,
                BlocksGameplay = true,
                CursorLocked = false,
            };

            var card = new VuiElement
            {
                Kind = VuiKind.Panel, Parent = root,
                Anchor = AnchorEnum.Center, W = 460, H = 190, Radius = 14,
                Bg = new[] { 0.10f, 0.09f, 0.11f, 0.98f },
            };
            root.Children.Add(card);

            card.Children.Add(new VuiElement
            {
                Kind = VuiKind.Text, Parent = card,
                Anchor = AnchorEnum.TopCenter, OffY = 24, W = 400, H = 30,
                Text = title ?? "", FontSize = 20, Weight = 700, Align = 1,
                Fg = new[] { 0.95f, 0.95f, 0.97f, 1f },
            });
            card.Children.Add(new VuiElement
            {
                Kind = VuiKind.Text, Parent = card,
                Anchor = AnchorEnum.TopCenter, OffY = 62, W = 410, H = 46,
                Text = message ?? "", FontSize = 14, Weight = 400, Align = 1,
                Fg = new[] { 0.78f, 0.78f, 0.82f, 1f },
            });
            card.Children.Add(new VuiElement
            {
                Kind = VuiKind.Button, Parent = card, Id = "__dlgYes",
                Anchor = AnchorEnum.BottomCenter, OffX = -85, OffY = -22, W = 150, H = 40, Radius = 9,
                Text = "Yes", FontSize = 15, Weight = 600,
                Bg = new[] { 0.28f, 0.46f, 0.30f, 1f }, Fg = new[] { 0.95f, 0.98f, 0.95f, 1f },
                ClickAction = "__dlg_yes",
            });
            card.Children.Add(new VuiElement
            {
                Kind = VuiKind.Button, Parent = card, Id = "__dlgNo",
                Anchor = AnchorEnum.BottomCenter, OffX = 85, OffY = -22, W = 150, H = 40, Radius = 9,
                Text = "No", FontSize = 15, Weight = 600,
                Bg = new[] { 0.42f, 0.26f, 0.26f, 1f }, Fg = new[] { 0.98f, 0.94f, 0.94f, 1f },
                ClickAction = "__dlg_no",
            });

            _dialog = new VuiCanvas { Root = root, Name = DialogScreenName };
            _dialog.Reindex();
            VuiStack.Instance.Show(_dialog);
        }

        /// <summary>Called by VuiStack when a dialog button fires — pop first, THEN invoke (the callback
        /// may push its own screens or open the next dialog).</summary>
        public static void HandleAction(string action)
        {
            var yes = _onYes; var no = _onNo;
            Close();
            try
            {
                if (action == "__dlg_yes") { if (yes != null) yes(); }
                else if (action == "__dlg_no") { if (no != null) no(); }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[VuiDialogs] callback: " + ex.Message); }
        }

        public static void Close()
        {
            if (_dialog != null) { VuiStack.Instance.Hide(_dialog); _dialog = null; }
            _onYes = null; _onNo = null;
        }
    }
}
