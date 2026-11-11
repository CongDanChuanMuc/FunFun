// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// In-process control channel used by the parity harness.
//
// UI Automation cannot drive this app reliably: MenuStrip invocations block
// while a modal dialog is open, and synthetic mouse clicks on dropdown items do
// not reach the handler.  The harness already injects this assembly into the
// target through DOTNET_STARTUP_HOOKS, so the honest alternative is to ask the
// app itself to perform the action: the channel runs on a worker thread and
// marshals each request to the UI thread.
//
// Commands are one line per connection, tab separated:
//   ping
//   tree                          dump every open form's control and menu tree
//   menu    <menu>\t<item>        PerformClick on a menu item
//   click   <form>\t<path>        PerformClick on a control
//   set     <form>\t<path>\t<text> set a control's Text
public static class ControlChannel
{
    private delegate bool EnumWindowProc(IntPtr handle, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetClassName(IntPtr handle, StringBuilder buffer, int length);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder buffer, int length);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    private static string logPath;

    public static void Start(string log)
    {
        logPath = log;
        string name = Environment.GetEnvironmentVariable("V250_CONTROL_PIPE");
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var thread = new Thread(() => Loop(name)) { IsBackground = true, Name = "v250-control" };
        thread.Start();
    }

    private static void Loop(string name)
    {
        while (true)
        {
            try
            {
                using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                           PipeTransmissionMode.Byte, PipeOptions.None))
                {
                    server.WaitForConnection();
                    using (var reader = new StreamReader(server, new UTF8Encoding(false)))
                    using (var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        // One command per connection: the client reads to EOF, so
                        // multi-line responses need no length prefix.
                        string line = reader.ReadLine();
                        string response;
                        try
                        {
                            response = Dispatch(line ?? "");
                        }
                        catch (Exception ex)
                        {
                            response = "ERR " + ex.GetType().Name + ": " + ex.Message;
                        }
                        writer.Write(response);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("channel " + ex.Message);
                Thread.Sleep(200);
            }
        }
    }

    private static string Dispatch(string line)
    {
        string[] parts = line.Split('\t');
        switch (parts[0])
        {
            case "ping":
                return "ok";
            case "tree":
                return OnUi(() => DumpTree());
            case "menu":
                return PerformMenu(parts[1], parts[2]);
            case "click":
                return PerformClick(int.Parse(parts[1]), parts[2]);
            case "set":
                return SetText(int.Parse(parts[1]), parts[2], parts[3]);
            case "dlg":
                return ClickDialogButton(parts[1]);
            case "ctx":
                return RightClickRow(int.Parse(parts[1]), parts[2], int.Parse(parts[3]), parts[4]);
            case "ctxname":
                return RightClickGridByName(parts[1], int.Parse(parts[2]), parts[3]);
            case "tbox":
                return SetToolStripText(parts[1], parts[2], parts[3]);
            case "setname":
                return SetTextByName(parts[1], parts[2]);
            case "clickname":
                return ClickByName(parts[1]);
            default:
                return "ERR unknown command " + parts[0];
        }
    }

    private static string OnUi(Func<string> action)
    {
        Form form = Application.OpenForms.Count > 0 ? Application.OpenForms[Application.OpenForms.Count - 1] : null;
        if (form == null)
        {
            return "ERR no form";
        }
        string result = null;
        Exception failure = null;
        form.Invoke(new Action(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        }));
        if (failure != null)
        {
            throw failure;
        }
        return result;
    }

    private static string PerformMenu(string menuText, string itemText)
    {
        Form host = null;
        ToolStripItem target = null;

        foreach (Form form in Application.OpenForms)
        {
            foreach (Control control in AllControls(form))
            {
                var strip = control as MenuStrip;
                if (strip == null)
                {
                    continue;
                }
                foreach (ToolStripItem top in strip.Items)
                {
                    if (top.Text != menuText)
                    {
                        continue;
                    }
                    var topItem = top as ToolStripMenuItem;
                    if (topItem == null)
                    {
                        continue;
                    }
                    foreach (ToolStripItem child in topItem.DropDownItems)
                    {
                        if (child.Text == itemText)
                        {
                            host = form;
                            target = child;
                            break;
                        }
                    }
                }
            }
        }

        if (target == null)
        {
            return "ERR menu item not found: " + menuText + "/" + itemText;
        }

        var dispatched = new ManualResetEventSlim(false);
        host.BeginInvoke(new Action(() =>
        {
            dispatched.Set();
            target.PerformClick();
        }));
        if (!dispatched.Wait(3000))
        {
            return "ERR dispatch timeout";
        }
        return "ok";
    }



    private static string PerformClick(int formIndex, string path)
    {
        Control control = Resolve(formIndex, path);
        if (control == null)
        {
            return "ERR no control";
        }
        var dispatched = new ManualResetEventSlim(false);
        control.BeginInvoke(new Action(() =>
        {
            dispatched.Set();
            var perform = control.GetType().GetMethod("PerformClick",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (perform != null)
            {
                perform.Invoke(control, null);
                return;
            }
            var onClick = typeof(Control).GetMethod("OnClick",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (onClick != null)
            {
                onClick.Invoke(control, new object[] { EventArgs.Empty });
            }
        }));
        dispatched.Wait(3000);
        return "ok";
    }

    private static string SetText(int formIndex, string path, string value)
    {
        return OnUi(() =>
        {
            Control control = Resolve(formIndex, path);
            if (control == null)
            {
                return "ERR no control";
            }
            control.Text = value.Replace("\\n", "\n").Replace("\\t", "\t");
            return "ok";
        });
    }

    /// <summary>
    /// Finds a control by its Name in any open form. Paths depend on the layout
    /// order, which a rewrite is free to change; names survive that.
    /// </summary>
    private static Control FindByName(string name)
    {
        // A dialog can reuse a name the main window already uses ("button1").
        // The active form wins, then the most recently opened one.
        var forms = new System.Collections.Generic.List<Form>();
        Form active = Form.ActiveForm;
        if (active != null)
        {
            forms.Add(active);
        }
        for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
        {
            Form form = Application.OpenForms[i];
            if (form != active)
            {
                forms.Add(form);
            }
        }

        foreach (Form form in forms)
        {
            foreach (Control control in AllControls(form))
            {
                if (control.Name == name)
                {
                    return control;
                }
            }
        }
        return null;
    }

    private static string SetTextByName(string name, string value)
    {
        return OnUi(() =>
        {
            Control control = FindByName(name);
            if (control == null)
            {
                return "ERR no control " + name;
            }
            control.Text = value.Replace("\\n", "\n").Replace("\\t", "\t");
            return "ok";
        });
    }

    private static string ClickByName(string name)
    {
        // Find the control on the UI thread, then click it without waiting: a
        // button whose handler opens a modal dialog never returns until the
        // dialog is dismissed, and the caller needs to reply in the meantime.
        Control control = null;
        var located = new ManualResetEventSlim(false);
        Form host = Form.ActiveForm;
        if (host == null && Application.OpenForms.Count > 0)
        {
            host = Application.OpenForms[Application.OpenForms.Count - 1];
        }
        if (host == null)
        {
            return "ERR no form";
        }

        host.BeginInvoke(new Action(() =>
        {
            control = FindByName(name);
            located.Set();
        }));
        if (!located.Wait(3000))
        {
            return "ERR lookup timeout";
        }
        if (control == null)
        {
            return "ERR no control " + name;
        }

        var dispatched = new ManualResetEventSlim(false);
        control.BeginInvoke(new Action(() =>
        {
            dispatched.Set();
            var perform = control.GetType().GetMethod("PerformClick",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (perform != null)
            {
                perform.Invoke(control, null);
                return;
            }
            InvokeProtected(control, "OnClick", EventArgs.Empty);
        }));
        dispatched.Wait(3000);
        return "ok";
    }

    // Native message boxes (MessageBox.Show) are not WinForms forms, so the
    // control tree cannot see them.  The UI thread is blocked inside Show, so
    // the click has to be sent as a window message from this worker thread.
    private static string ClickDialogButton(string text)
    {
        IntPtr dialog = IntPtr.Zero;
        uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        EnumWindows((handle, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != self || !IsWindowVisible(handle))
            {
                return true;
            }
            var cls = new StringBuilder(64);
            GetClassName(handle, cls, cls.Capacity);
            if (cls.ToString() == "#32770")
            {
                dialog = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (dialog == IntPtr.Zero)
        {
            return "ERR no dialog";
        }

        IntPtr button = IntPtr.Zero;
        EnumChildWindows(dialog, (handle, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(handle, cls, cls.Capacity);
            if (cls.ToString() != "Button")
            {
                return true;
            }
            var label = new StringBuilder(256);
            GetWindowText(handle, label, label.Capacity);
            if (label.ToString().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                button = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (button == IntPtr.Zero)
        {
            return "ERR no button " + text;
        }

        SendMessage(button, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
        return "ok";
    }

    // The account grid has no menu of its own: the context menu is built on
    // demand inside the MouseDown handler.  Raise that handler with a synthetic
    // right-button press on the requested row, then click the item that appears.
    private static string RightClickRow(int formIndex, string path, int row, string itemText)
    {
        var grid = Resolve(formIndex, path) as DataGridView;
        if (grid == null)
        {
            return "ERR no grid";
        }
        return RightClickGrid(grid, row, itemText);
    }

    private static string RightClickGridByName(string name, int row, string itemText)
    {
        var grid = FindByName(name) as DataGridView;
        if (grid == null)
        {
            return "ERR no grid " + name;
        }
        return RightClickGrid(grid, row, itemText);
    }

    private static string RightClickGrid(DataGridView grid, int row, string itemText)
    {

        string failure = null;
        var done = new ManualResetEventSlim(false);
        grid.Invoke(new Action(() =>
        {
            try
            {
                if (row < 0 || row >= grid.Rows.Count)
                {
                    failure = "ERR row " + row + " of " + grid.Rows.Count;
                    return;
                }
                try { grid.FirstDisplayedScrollingRowIndex = row; } catch { }
                Rectangle rect = grid.GetCellDisplayRectangle(0, row, false);
                if (rect.Width == 0 || rect.Height == 0)
                {
                    failure = "ERR row not visible";
                    return;
                }
                var point = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                int handlers = InvokeProtected(grid, "OnMouseDown",
                    new MouseEventArgs(MouseButtons.Right, 1, point.X, point.Y, 0));
                if (handlers != 0)
                {
                    failure = "ERR OnMouseDown " + handlers;
                    return;
                }
            }
            catch (Exception ex)
            {
                failure = "ERR " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                done.Set();
            }
        }));
        done.Wait(3000);
        if (failure != null)
        {
            return failure;
        }

        ToolStripDropDown down = null;
        for (int attempt = 0; attempt < 40 && down == null; attempt++)
        {
            Thread.Sleep(50);
            down = FindOpenDropDown();
        }
        if (down == null)
        {
            return "ERR no context menu";
        }

        List<string> labels = new List<string>();
        ToolStripItem target = null;
        foreach (ToolStripItem item in down.Items)
        {
            labels.Add(item.Text);
            if (item.Text == itemText)
            {
                target = item;
            }
        }
        if (target == null)
        {
            down.Close();
            return "ERR no item " + itemText + " in [" + string.Join("|", labels.ToArray()) + "]";
        }

        down.BeginInvoke(new Action(() => target.PerformClick()));
        Thread.Sleep(150);
        return "ok " + itemText + " from [" + string.Join("|", labels.ToArray()) + "]";
    }

    private static ToolStripDropDown FindOpenDropDown()
    {
        ToolStripDropDown found = null;
        uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        EnumWindows((handle, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != self || !IsWindowVisible(handle))
            {
                return true;
            }
            var down = Control.FromHandle(handle) as ToolStripDropDown;
            if (down != null)
            {
                found = down;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Settings -> Tên cửa sổ is a submenu holding a ToolStripTextBox; the value
    // is committed by pressing Enter inside it.
    private static string SetToolStripText(string menuText, string subText, string value)
    {
        ToolStripTextBox textBox = null;
        Form host = null;

        foreach (Form form in Application.OpenForms)
        {
            foreach (Control control in AllControls(form))
            {
                var strip = control as MenuStrip;
                if (strip == null)
                {
                    continue;
                }
                foreach (ToolStripItem top in strip.Items)
                {
                    if (top.Text != menuText)
                    {
                        continue;
                    }
                    var menu = top as ToolStripMenuItem;
                    if (menu == null)
                    {
                        continue;
                    }
                    foreach (ToolStripItem child in menu.DropDownItems)
                    {
                        if (child.Text != subText && child.Name != subText)
                        {
                            continue;
                        }
                        var direct = child as ToolStripTextBox;
                        if (direct != null)
                        {
                            textBox = direct;
                        }
                        var submenu = child as ToolStripMenuItem;
                        if (submenu != null)
                        {
                            foreach (ToolStripItem leaf in submenu.DropDownItems)
                            {
                                if (leaf is ToolStripTextBox)
                                {
                                    textBox = (ToolStripTextBox)leaf;
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }
            }
            if (textBox != null)
            {
                host = form;
                break;
            }
        }

        if (textBox == null)
        {
            return "ERR no tool strip text box";
        }

        var dispatched = new ManualResetEventSlim(false);
        host.BeginInvoke(new Action(() =>
        {
            textBox.Text = value;
            InvokeProtected(textBox.Control, "OnKeyDown", new KeyEventArgs(Keys.Enter));
            dispatched.Set();
        }));
        if (!dispatched.Wait(3000))
        {
            return "ERR dispatch timeout";
        }
        return "ok";
    }

    // Calls a protected On<Event> method so the real handler chain runs, without
    // depending on obfuscated handler names or on private event-key fields whose
    // spelling differs between .NET Framework and modern WinForms.
    // Returns 0 on success, or an error string.
    private static int InvokeProtected(object target, string method, EventArgs args)
    {
        MethodInfo info = target.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (info == null)
        {
            return -1;
        }
        info.Invoke(target, new object[] { args });
        return 0;
    }

    private static Control Resolve(int formIndex, string path)
    {
        if (formIndex < 0 || formIndex >= Application.OpenForms.Count)
        {
            return null;
        }
        Control current = Application.OpenForms[formIndex];
        if (path.Length == 0)
        {
            return current;
        }
        foreach (string part in path.Split('.'))
        {
            int index = int.Parse(part);
            if (index >= current.Controls.Count)
            {
                return null;
            }
            current = current.Controls[index];
        }
        return current;
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in AllControls(child))
            {
                yield return nested;
            }
        }
    }

    private static string DumpTree()
    {
        var sb = new StringBuilder();
        for (int f = 0; f < Application.OpenForms.Count; f++)
        {
            Form form = Application.OpenForms[f];
            sb.Append("form\t").Append(f).Append('\t')
              .Append(form.GetType().Name).Append('\t')
              .Append(Escape(form.Text)).Append('\t')
              .Append(form.Visible).Append('\n');
            Walk(form, f, "", 1, sb);
        }
        return sb.ToString();
    }

    private static void Walk(Control control, int formIndex, string path, int depth, StringBuilder sb)
    {
        sb.Append("ctl\t").Append(formIndex).Append('\t').Append(path).Append('\t')
          .Append(control.GetType().Name).Append('\t')
          .Append(Escape(control.Name)).Append('\t')
          .Append(Escape(control.Text)).Append('\t')
          .Append(control.Visible).Append('\t').Append(control.Enabled).Append('\n');

        var strip = control as MenuStrip;
        if (strip != null)
        {
            foreach (ToolStripItem top in strip.Items)
            {
                sb.Append("mnu\t").Append(formIndex).Append('\t').Append(Escape(top.Text)).Append('\t')
                  .Append(top.Enabled).Append('\n');
                var topItem = top as ToolStripMenuItem;
                if (topItem == null)
                {
                    continue;
                }
                foreach (ToolStripItem child in topItem.DropDownItems)
                {
                    sb.Append("itm\t").Append(formIndex).Append('\t').Append(Escape(top.Text)).Append('\t')
                      .Append(Escape(child.Text)).Append('\t').Append(child.Enabled).Append('\n');
                }
            }
        }

        for (int i = 0; i < control.Controls.Count; i++)
        {
            string childPath = path.Length == 0 ? i.ToString() : path + "." + i;
            Walk(control.Controls[i], formIndex, childPath, depth + 1, sb);
        }
    }

    private static string Escape(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        return value.Replace("\\", "\\\\").Replace("\t", " ").Replace("\n", " ").Replace("\r", " ");
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(logPath, DateTime.Now.ToString("o") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}

// ===========================================================================
// Credited by TapLamDang
// Public, free at https://www.facebook.com/profile.php?id=61596126010538
// If you paid for this, you are getting scammed.
// ===========================================================================
