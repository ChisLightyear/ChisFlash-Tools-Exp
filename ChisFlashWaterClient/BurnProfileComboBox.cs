using System.Runtime.InteropServices;

namespace ChisFlashWaterClient;

internal sealed class BurnProfileComboBox : ComboBox
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<object?, bool>? CanSelectItem { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<object?, bool>? IsGroupItem { get; set; }
    private object? lastChoice;
    private bool restoringChoice;
    private int navigationDirection;
    private readonly PopupListWindow popupList;

    public BurnProfileComboBox()
    {
        popupList = new PopupListWindow(this);
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Math.Max(18, Font.Height + 4);
        MaxDropDownItems = 18;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = Math.Max(18, Font.Height + 4);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        navigationDirection = e.KeyCode switch
        {
            Keys.Down or Keys.PageDown or Keys.Home => 1,
            Keys.Up or Keys.PageUp or Keys.End => -1,
            _ => 0,
        };
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); navigationDirection = 0; }
    protected override void OnMouseDown(MouseEventArgs e) { navigationDirection = 0; base.OnMouseDown(e); }
    protected override void OnDropDown(EventArgs e)
    {
        navigationDirection = 0;
        var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
        if (GetComboBoxInfo(Handle, ref info) && info.List != IntPtr.Zero && info.List != popupList.Handle)
        {
            popupList.Detach();
            popupList.AssignHandle(info.List);
        }
        base.OnDropDown(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        popupList?.Detach();
        base.OnHandleDestroyed(e);
    }
    protected override void OnMouseWheel(MouseEventArgs e) { navigationDirection = e.Delta < 0 ? 1 : -1; base.OnMouseWheel(e); navigationDirection = 0; }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        if (restoringChoice) return;
        if (SelectedIndex >= 0 && CanSelectItem?.Invoke(SelectedItem) == false)
        {
            int previous = lastChoice is null ? -1 : Items.IndexOf(lastChoice);
            int next = -1;
            if (navigationDirection != 0)
                for (int index = SelectedIndex + navigationDirection; index >= 0 && index < Items.Count; index += navigationDirection)
                    if (CanSelectItem(Items[index])) { next = index; break; }
            if (next < 0) next = previous;
            if (next < 0)
                for (int index = 0; index < Items.Count; index++)
                    if (CanSelectItem(Items[index])) { next = index; break; }
            restoringChoice = true;
            try { SelectedIndex = next; }
            finally { restoringChoice = false; }
            // 点中分组或待确认型号时，不触发工作区重建。
            if (next == previous || next < 0) return;
        }
        if (SelectedIndex >= 0) lastChoice = SelectedItem;
        base.OnSelectedIndexChanged(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        object? item = e.Index >= 0 && e.Index < Items.Count ? Items[e.Index] : SelectedItem;
        bool group = IsGroupItem?.Invoke(item) == true;
        bool available = Enabled && CanSelectItem?.Invoke(item) != false;
        if (available) e.DrawBackground();
        else
        {
            using var background = new SolidBrush(SystemColors.Window);
            e.Graphics.FillRectangle(background, e.Bounds);
        }
        var bounds = new Rectangle(e.Bounds.X + (group ? 4 : 12), e.Bounds.Y, Math.Max(0, e.Bounds.Width - (group ? 8 : 16)), e.Bounds.Height);
        using var font = new Font(e.Font ?? Font, group ? FontStyle.Bold : FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics, GetItemText(item), font, bounds, available ? e.ForeColor : SystemColors.GrayText,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (available) e.DrawFocusRectangle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRect ItemBounds, ButtonBounds;
        public int ButtonState;
        public IntPtr Combo, Item, List;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr handle, ref ComboBoxInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class PopupListWindow(BurnProfileComboBox owner) : NativeWindow
    {
        public void Detach() { if (Handle != IntPtr.Zero) ReleaseHandle(); }

        protected override void WndProc(ref Message message)
        {
            // 在原生下拉列表处理鼠标前拦截，分组行既不高亮，也不响应按下、松开或双击。
            if (message.Msg is 0x0200 or 0x0201 or 0x0202 or 0x0203)
            {
                long hit = SendMessage(Handle, 0x01A9, IntPtr.Zero, message.LParam).ToInt64();
                int index = (int)(hit & 0xFFFF);
                bool outside = (hit & 0xFFFF0000) != 0;
                if (!outside && index < owner.Items.Count && owner.CanSelectItem?.Invoke(owner.Items[index]) == false)
                {
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref message);
        }
    }
}
