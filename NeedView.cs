using System.Drawing;
using System.Windows.Forms;

namespace ScweenSpit;

/// <summary>
/// What is going on with one need here: an answer being sent, why the last attempt failed, what
/// has been typed or ticked, whether its details are open. Kept by need rather than by whatever
/// shows it, so a card and the list agree, and nothing is lost when either is rebuilt.
/// </summary>
internal sealed class NeedState
{
    public bool Sending;
    public string? Failed;
    public string? Draft;
    public readonly HashSet<string> Picks = new(StringComparer.Ordinal);
    public bool Expanded;
    public int Version;
}

internal static class NeedStates
{
    private static readonly Dictionary<string, NeedState> states = new(StringComparer.Ordinal);

    /// <summary>Raised, on the UI thread, with the key of a need whose state changed.</summary>
    public static event Action<string>? Changed;

    /// <summary>
    /// How many passkey prompts are up. Each takes the focus, and nothing that owns one may put
    /// itself away underneath it.
    /// </summary>
    public static int Prompting { get; private set; }

    public static NeedState For(string key)
    {
        if (!states.TryGetValue(key, out var state)) states[key] = state = new NeedState();
        return state;
    }

    public static void Touch(string key)
    {
        For(key).Version++;
        Changed?.Invoke(key);
    }

    /// <summary>Drops the state of needs no longer listed.</summary>
    public static void Keep(IEnumerable<string> keys)
    {
        var keep = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var gone in states.Keys.Where(k => !keep.Contains(k)).ToList()) states.Remove(gone);
    }

    /// <summary>Runs one of a need's actions, showing it as in progress and any failure on the need.</summary>
    public static async void Run(string key, Func<Task<string?>> action, bool prompt = false)
    {
        var state = For(key);
        if (state.Sending) return;
        state.Sending = true;
        state.Failed = null;
        if (prompt) Prompting++;
        Touch(key);

        string? error;
        try { error = await action(); }
        catch (Exception ex) { error = ex.Message; }

        if (prompt) Prompting--;
        state.Sending = false;
        if (error is null) { state.Draft = null; state.Picks.Clear(); }
        else state.Failed = error;
        Touch(key);
    }
}

/// <summary>
/// One need, laid out so what it is and what to do about it read at a glance: what kind and how
/// long, the title, a line saying plainly what is wanted, then the buttons that do it. The detail
/// opens out on request. The same view is a floating card and a row of the list.
/// </summary>
internal sealed class NeedView : FlowLayoutPanel
{
    private readonly int width;
    private readonly double scale;
    private readonly Dictionary<(string, float, FontStyle), Font> fonts = [];
    private string builtKey = "";
    private Label? age;

    public OrchestraNeedItem Item { get; private set; }

    /// <summary>Raised after the view was rebuilt, which may have changed its height.</summary>
    public event Action? Rebuilt;

    public NeedView(OrchestraNeedItem item, int width, double scale)
    {
        Item = item;
        this.width = width;
        this.scale = scale;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Panel;
        Padding = new Padding(S(14), S(10), S(12), S(10));
        Margin = new Padding(0);
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);
        Cursor = Cursors.Hand;

        NeedStates.Changed += OnStateChanged;
        Click += (_, _) => ToggleDetails();
        Rebuild();
    }

    private string Key => NeedActions.Key(Item);

    private int S(int px) => (int)Math.Round(px * scale);

    // Labels do not dispose the fonts they are given, so they are made once per size and kept.
    private Font Px(float px, FontStyle style = FontStyle.Regular, string face = "Segoe UI")
    {
        if (!fonts.TryGetValue((face, px, style), out var font))
            fonts[(face, px, style)] = font = new Font(face, (float)(px * scale), style, GraphicsUnit.Pixel);
        return font;
    }

    /// <summary>The need as the feed now says it; rebuilt only if that changes what is shown.</summary>
    public void Set(OrchestraNeedItem item)
    {
        Item = item;
        Rebuild();
    }

    /// <summary>Moves the "waiting 4m" on without rebuilding anything.</summary>
    public void RefreshAge()
    {
        if (age is { IsDisposed: false }) age.Text = AgeText();
    }

    private void OnStateChanged(string key)
    {
        // Not straight away: the rebuild disposes the very button whose click changed the state.
        if (key == Key && IsHandleCreated && !IsDisposed) BeginInvoke(() => Rebuild());
    }

    private void ToggleDetails()
    {
        if (!HasDetails) return;
        var state = NeedStates.For(Key);
        state.Expanded = !state.Expanded;
        NeedStates.Touch(Key);
    }

    private bool HasDetails => Item.Need.Detail is { Length: > 0 };

    private Color Accent => !Item.Waiting ? Theme.Muted : Item.Need.Blocking ? OrchestraStrip.BlockingColour : OrchestraStrip.NeedColour;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var bar = new SolidBrush(Accent);
        e.Graphics.FillRectangle(bar, 0, 0, S(5), Height);
    }

    private string AgeText() =>
        Item.Age(Environment.TickCount64) is { } a ? (a.TotalMinutes < 1 ? "just now" : $"waiting {OrchestraUsage.FormatAge(a)}") : "";

    private void Rebuild(bool force = false)
    {
        if (IsDisposed) return;
        var state = NeedStates.For(Key);
        string key = $"{Item.Need.GetHashCode()}:{Item.Done}:{Item.Feed.Stale}:{state.Version}";
        if (!force && key == builtKey) return;
        builtKey = key;

        SuspendLayout();
        while (Controls.Count > 0) Controls[0].Dispose();

        var need = Item.Need;
        bool done = Item.Done;
        int inner = width - Padding.Horizontal;
        bool approval = NeedActions.IsApproval(need);
        bool native = approval && need.ApproveApi && !done;

        // 1. What kind, whether it holds anything up, how long it has waited.
        var top = Row();
        top.Controls.Add(Chip(OrchestraUsage.NeedKindLabel(need.Kind).ToUpperInvariant(), done ? Theme.Muted : OrchestraStrip.NeedColour));
        if (need.Blocking && !done) top.Controls.Add(Chip("BLOCKING", OrchestraStrip.BlockingColour));
        if (done) top.Controls.Add(Chip("DONE", Theme.Muted));
        age = Words(AgeText(), 12, FontStyle.Regular, Theme.Muted, inner);
        age.Margin = new Padding(S(2), S(2), 0, 0);
        top.Controls.Add(age);
        Controls.Add(top);

        // 2. The title, big enough to read from across the desk.
        var title = Words(need.Title, 16, FontStyle.Bold, done ? Theme.Muted : Theme.Text, inner);
        title.Margin = new Padding(0, S(6), 0, S(2));
        Controls.Add(title);
        if (need.Where is { Length: > 0 } where)
            Controls.Add(Words(where + (Item.Feed.Stale ? " · feed not answering, may be out of date" : ""), 12, FontStyle.Regular, Theme.Muted, inner));

        // 3. In plain words, what is wanted of you.
        if (!done)
        {
            var todo = new Label
            {
                Text = NeedActions.WhatToDo(need), AutoSize = true, MaximumSize = new Size(inner, 0),
                MinimumSize = new Size(inner, 0), Font = Px(13.5f), ForeColor = Theme.Text, BackColor = Theme.Raised,
                Padding = new Padding(S(8), S(6), S(8), S(6)), Margin = new Padding(0, S(8), 0, S(6)), UseMnemonic = false,
            };
            Controls.Add(todo);
        }

        // 4. The detail, on request.
        if (HasDetails)
        {
            if (state.Expanded)
            {
                var detail = Words(need.Detail!, 12.5f, FontStyle.Regular, Theme.Muted, inner);
                detail.Margin = new Padding(0, 0, 0, S(2));
                Controls.Add(detail);
            }
            var toggle = new LinkLabel
            {
                Text = state.Expanded ? "Hide details ▴" : "Details ▾", AutoSize = true, Font = Px(12),
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, LinkBehavior = LinkBehavior.HoverUnderline,
                Margin = new Padding(0, 0, 0, S(6)),
            };
            toggle.LinkClicked += (_, _) => ToggleDetails();
            Controls.Add(toggle);
        }

        // 5. A command to run is shown whole, ready to copy.
        if (need.Command is { } command && !done)
            Controls.Add(new TextBox
            {
                Text = command, ReadOnly = true, Width = inner, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Window, ForeColor = Theme.Text, Font = Px(12.5f, FontStyle.Regular, "Consolas"),
                Margin = new Padding(0, 0, 0, S(6)),
            });

        // 6. A question's answers, each one a button that sends it.
        if (need.Question is { } question && !done) AddQuestion(question, state, inner);

        // 7. The buttons, the one that does what is wanted first and coloured.
        var actions = Row();
        actions.WrapContents = true;
        actions.MaximumSize = new Size(inner, 0);
        actions.Margin = new Padding(0, S(4), 0, 0);
        bool busy = state.Sending;

        if (native)
        {
            var approve = MakeButton(busy ? "Waiting for passkey…" : "Approve with passkey", primary: true, S(190));
            approve.Enabled = !busy;
            approve.Click += (_, _) => NeedStates.Run(Key, () => OrchestraUsage.ApproveWithPasskeyAsync(Item, FindForm()?.Handle ?? IntPtr.Zero), prompt: true);
            actions.Controls.Add(approve);
        }
        if (NeedActions.Url(need) is not null)
        {
            string label = native ? "Open in console" : approval ? "Approve in console →" : "Open page →";
            bool primary = !done && !native && (approval || (need.Command is null && need.Question is null));
            var open = MakeButton(label, primary, approval ? S(176) : S(124));
            open.Click += (_, _) => NeedActions.OpenLatest(Item);
            actions.Controls.Add(open);
        }
        if (need.Command is { } toCopy && !done)
        {
            var copy = MakeButton("Copy command", primary: true, S(132));
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(toCopy); copy.Text = "Copied ✓"; }
                catch (Exception ex) { Log.Write($"could not copy need command: {ex.Message}"); copy.Text = "Copy failed"; }
            };
            actions.Controls.Add(copy);
        }
        if (done)
        {
            var waiting = Words("Done, waiting for the orchestrator to confirm", 12.5f, FontStyle.Italic, Theme.Muted, inner);
            waiting.Margin = new Padding(0, S(6), 0, 0);
            actions.Controls.Add(waiting);
        }
        else
        {
            var mark = MakeButton(busy && !native ? "Sending…" : "Mark done", primary: false, S(112));
            mark.Enabled = !busy;
            mark.Click += (_, _) => NeedStates.Run(Key, () => OrchestraUsage.MarkDoneAsync(Item));
            actions.Controls.Add(mark);
        }
        Controls.Add(actions);

        if (state.Failed is { } why)
        {
            var failed = Words(why == "cancelled" ? "Passkey prompt dismissed: nothing was sent." : $"Didn't go through: {why}",
                              12.5f, FontStyle.Regular, OrchestraStrip.BlockingColour, inner);
            failed.Margin = new Padding(0, S(6), 0, 0);
            Controls.Add(failed);
        }

        // Clicking anywhere that is not a control opens or closes the details.
        foreach (Control c in Controls)
            if (c != actions) Wire(c);

        ResumeLayout(true);
        Invalidate();
        Rebuilt?.Invoke();
    }

    private void Wire(Control c)
    {
        if (c is Button or TextBox or LinkLabel or CheckBox) return;
        c.Cursor = Cursors.Hand;
        c.Click += (_, _) => ToggleDetails();
        foreach (Control child in c.Controls) Wire(child);
    }

    private void AddQuestion(NeedQuestion question, NeedState state, int inner)
    {
        bool busy = state.Sending;
        foreach (var choice in question.Choices)
        {
            if (question.Multi)
            {
                var tick = new CheckBox
                {
                    Text = choice.Label, Checked = state.Picks.Contains(choice.Id), AutoSize = true, Enabled = !busy,
                    ForeColor = Theme.Text, Font = Px(13.5f), FlatStyle = FlatStyle.Flat, UseMnemonic = false,
                    Margin = new Padding(0, S(2), 0, choice.Description is null ? S(2) : 0), Cursor = Cursors.Hand,
                };
                tick.CheckedChanged += (_, _) => { if (tick.Checked) state.Picks.Add(choice.Id); else state.Picks.Remove(choice.Id); };
                Controls.Add(tick);
            }
            else
            {
                var pick = new Button
                {
                    Text = choice.Label, AutoSize = false, Size = new Size(inner, S(34)), Font = Px(13.5f, FontStyle.Bold),
                    FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = Theme.Raised, Cursor = Cursors.Hand,
                    Margin = new Padding(0, S(3), 0, S(1)), TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(S(8), 0, 0, 0), UseMnemonic = false, Enabled = !busy,
                };
                pick.FlatAppearance.BorderSize = 1;
                pick.FlatAppearance.BorderColor = Theme.Divider;
                pick.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Theme.Accent, 0.1f);
                pick.Click += (_, _) => NeedStates.Run(Key, () => OrchestraUsage.AnswerAsync(Item, [choice.Id], state.Draft));
                Controls.Add(pick);
            }
            if (choice.Description is { } about)
            {
                var line = Words(about, 12, FontStyle.Regular, Theme.Muted, inner - S(8));
                line.Margin = new Padding(S(question.Multi ? 20 : 8), 0, 0, S(4));
                Controls.Add(line);
            }
        }

        if (question.AllowText)
        {
            var typed = new TextBox
            {
                Text = state.Draft ?? "", Width = inner, BorderStyle = BorderStyle.FixedSingle, Enabled = !busy,
                BackColor = Theme.Window, ForeColor = Theme.Text, Font = Px(13.5f),
                PlaceholderText = question.Choices.Count > 0 ? "Or type an answer…" : "Type an answer…",
                Margin = new Padding(0, S(4), 0, S(2)),
            };
            typed.TextChanged += (_, _) => state.Draft = typed.Text;
            typed.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter || string.IsNullOrWhiteSpace(typed.Text)) return;
                e.SuppressKeyPress = true;
                NeedStates.Run(Key, () => OrchestraUsage.AnswerAsync(Item, question.Multi ? state.Picks.ToList() : [], typed.Text));
            };
            Controls.Add(typed);
        }

        if (question.Multi || question.AllowText)
        {
            var send = MakeButton(busy ? "Sending…" : "Send answer", primary: true, S(124));
            send.Enabled = !busy;
            send.Margin = new Padding(0, S(4), 0, S(4));
            send.Click += (_, _) =>
            {
                var chosen = question.Multi ? state.Picks.ToList() : [];
                if (chosen.Count == 0 && string.IsNullOrWhiteSpace(state.Draft)) return;
                NeedStates.Run(Key, () => OrchestraUsage.AnswerAsync(Item, chosen, state.Draft));
            };
            Controls.Add(send);
        }
    }

    private FlowLayoutPanel Row() => new()
    {
        AutoSize = true, WrapContents = false, BackColor = Theme.Panel, Margin = new Padding(0),
    };

    private Label Words(string text, float px, FontStyle style, Color colour, int max) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(max, 0), ForeColor = colour, Font = Px(px, style),
        Margin = new Padding(0), UseMnemonic = false,
    };

    private Label Chip(string text, Color colour) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.Window, BackColor = colour, Font = Px(11.5f, FontStyle.Bold),
        Padding = new Padding(S(5), S(2), S(5), S(2)), Margin = new Padding(0, 0, S(6), 0), UseMnemonic = false,
    };

    private Button MakeButton(string text, bool primary, int buttonWidth)
    {
        var b = new System.Windows.Forms.Button
        {
            Text = text, AutoSize = false, Size = new Size(buttonWidth, S(32)), Font = Px(13.5f, primary ? FontStyle.Bold : FontStyle.Regular),
            FlatStyle = FlatStyle.Flat, ForeColor = primary ? Color.White : Theme.Text,
            BackColor = primary ? Theme.Accent : Theme.Raised, Cursor = Cursors.Hand,
            Margin = new Padding(0, S(2), S(8), S(2)), TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Divider;
        return b;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NeedStates.Changed -= OnStateChanged;
            foreach (var font in fonts.Values) font.Dispose();
            fonts.Clear();
        }
        base.Dispose(disposing);
    }
}
