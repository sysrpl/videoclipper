using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using videoclipper.Services;

namespace videoclipper.Controls;

/// <summary>
/// A timeline with IN, OUT and NOW markers, and a Premiere-style zoom scrollbar below it, drawn as
/// one control. Click or drag the timeline to seek. Drag either end of the blue scrollbar to zoom;
/// drag its middle to scroll.
/// </summary>
public sealed class ZoomTimeline : Control
{
    private const double EdgeMargin = 14.0;
    private static readonly double[] TickSteps =
        [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 3600];

    private static readonly IBrush BackgroundBrush = Rgb(0.12, 0.13, 0.15);
    private static readonly IBrush TrackBrush = Rgb(0.20, 0.21, 0.24);
    private static readonly IBrush SelectionBrush = Rgb(0.18, 0.55, 0.85, 0.22);
    private static readonly IPen TickPen = new Pen(Rgb(0.48, 0.50, 0.54), 1);
    private static readonly IBrush TickTextBrush = Rgb(0.80, 0.81, 0.84);
    private static readonly IBrush InBrush = Rgb(0.30, 0.82, 0.43);
    private static readonly IBrush OutBrush = Rgb(0.95, 0.39, 0.32);
    private static readonly IBrush NowBrush = Rgb(0.25, 0.67, 1.0);
    private static readonly IBrush ScrollTrackBrush = Rgb(0.27, 0.28, 0.31);
    private static readonly IBrush ScrollViewBrush = Rgb(0.23, 0.55, 0.82);
    private static readonly IBrush HandleBrush = Rgb(0.82, 0.89, 0.96);
    private static readonly IPen HandleGripPen = new Pen(Rgb(0.32, 0.42, 0.52), 1);

    private enum DragAction { None, Seek, ResizeLeft, ResizeRight, Pan }

    private double _duration = 1.0;
    private double _frameRate = 30.0;
    private double _visibleStart;
    private double _visibleEnd = 1.0;
    private double _position;
    private double _trimStart;
    private double _trimEnd = 1.0;
    private DragAction _drag;
    private double _dragX;
    private double _dragStart;
    private double _dragEnd;
    private Cursor? _resizeCursor;
    private Cursor? _panCursor;
    private Cursor? _seekCursor;

    public ZoomTimeline()
    {
        Height = 94;
        IsEnabled = false;
        ClipToBounds = true;
    }

    /// <summary>Raised when the user clicks or drags the timeline. Final is false while still dragging.</summary>
    public event Action<double, bool>? SeekRequested;

    public double Position => _position;

    public void SetMedia(double duration, double frameRate)
    {
        _duration = Math.Max(0.001, duration);
        _frameRate = Math.Max(1.0, frameRate);
        _visibleStart = 0.0;
        _visibleEnd = _duration;
        _position = 0.0;
        _trimStart = 0.0;
        _trimEnd = _duration;
        IsEnabled = true;
        InvalidateVisual();
    }

    public void SetPosition(double position)
    {
        position = Math.Clamp(position, 0.0, _duration);
        if (Math.Abs(position - _position) > 0.0001)
        {
            _position = position;
            InvalidateVisual();
        }
    }

    public void SetTrim(double start, double end)
    {
        _trimStart = Math.Clamp(start, 0.0, _duration);
        _trimEnd = Math.Clamp(end, 0.0, _duration);
        InvalidateVisual();
    }

    private (double Left, double Right) HorizontalBounds() => (EdgeMargin, Math.Max(15.0, Bounds.Width - EdgeMargin));

    private double TimeToX(double value)
    {
        var (left, right) = HorizontalBounds();
        var span = Math.Max(0.000001, _visibleEnd - _visibleStart);
        return left + (value - _visibleStart) * (right - left) / span;
    }

    private double XToTime(double x)
    {
        var (left, right) = HorizontalBounds();
        var fraction = (Math.Clamp(x, left, right) - left) / Math.Max(1.0, right - left);
        return _visibleStart + fraction * (_visibleEnd - _visibleStart);
    }

    private double OverviewX(double value)
    {
        var (left, right) = HorizontalBounds();
        return left + value * (right - left) / _duration;
    }

    private double ChooseTickStep()
    {
        var target = (_visibleEnd - _visibleStart) / 8.0;
        foreach (var step in TickSteps)
        {
            if (step >= target)
                return step;
        }
        return 7200.0;
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var (left, right) = HorizontalBounds();
        double timelineTop = 25.0, timelineBottom = height - 27.0;
        double scrollbarY = height - 19.0, scrollbarHeight = 14.0;

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, width, height));
        context.FillRectangle(TrackBrush, new Rect(left, timelineTop, right - left, timelineBottom - timelineTop));

        var selectionLeft = Math.Max(left, TimeToX(_trimStart));
        var selectionRight = Math.Min(right, TimeToX(_trimEnd));
        if (selectionRight > selectionLeft)
        {
            context.FillRectangle(SelectionBrush,
                new Rect(selectionLeft, timelineTop, selectionRight - selectionLeft, timelineBottom - timelineTop));
        }

        var step = ChooseTickStep();
        var tick = Math.Floor(_visibleStart / step) * step;
        if (tick < _visibleStart)
            tick += step;
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty));
        while (tick <= _visibleEnd + step * 0.01)
        {
            var x = TimeToX(tick);
            context.DrawLine(TickPen, new Point(x, timelineTop), new Point(x, timelineBottom));
            var label = Text(VideoCore.FormatTime(tick), typeface, 11, TickTextBrush);
            context.DrawText(label, new Point(Math.Min(x + 3, right - 48), timelineBottom - 5 - label.Height));
            tick += step;
        }

        var bold = new Typeface(typeface.FontFamily, weight: FontWeight.Bold);
        DrawMarker(context, _trimStart, "IN", InBrush, timelineTop, timelineBottom, bold);
        DrawMarker(context, _trimEnd, "OUT", OutBrush, timelineTop, timelineBottom, bold);
        DrawMarker(context, _position, "NOW", NowBrush, timelineTop, timelineBottom, bold);

        context.FillRectangle(ScrollTrackBrush, new Rect(left, scrollbarY, right - left, scrollbarHeight));
        var viewLeft = OverviewX(_visibleStart);
        var viewRight = OverviewX(_visibleEnd);
        context.FillRectangle(ScrollViewBrush, new Rect(viewLeft, scrollbarY, Math.Max(1, viewRight - viewLeft), scrollbarHeight));
        foreach (var handleX in new[] { viewLeft, viewRight })
        {
            context.FillRectangle(HandleBrush, new Rect(handleX - 5, scrollbarY - 1, 10, scrollbarHeight + 2));
            context.DrawLine(HandleGripPen, new Point(handleX - 2, scrollbarY + 3), new Point(handleX - 2, scrollbarY + scrollbarHeight - 3));
            context.DrawLine(HandleGripPen, new Point(handleX + 2, scrollbarY + 3), new Point(handleX + 2, scrollbarY + scrollbarHeight - 3));
        }
    }

    private void DrawMarker(DrawingContext context, double value, string label, IBrush brush,
        double top, double bottom, Typeface typeface)
    {
        if (value < _visibleStart || value > _visibleEnd)
            return;
        var x = TimeToX(value);
        context.DrawLine(new Pen(brush, 2), new Point(x, top - 1), new Point(x, bottom));
        var arrow = new StreamGeometry();
        using (var geometry = arrow.Open())
        {
            geometry.BeginFigure(new Point(x - 6, top - 9), isFilled: true);
            geometry.LineTo(new Point(x + 6, top - 9));
            geometry.LineTo(new Point(x, top - 1));
            geometry.EndFigure(isClosed: true);
        }
        context.DrawGeometry(brush, null, arrow);
        var text = Text(label, typeface, 10, brush);
        context.DrawText(text, new Point(Math.Max(2, x - 10), top - 12 - text.Height + 2));
    }

    private (double Top, double ViewLeft, double ViewRight) ScrollbarGeometry() =>
        (Bounds.Height - 22.0, OverviewX(_visibleStart), OverviewX(_visibleEnd));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        var (scrollbarTop, viewLeft, viewRight) = ScrollbarGeometry();
        _dragX = point.X;
        _dragStart = _visibleStart;
        _dragEnd = _visibleEnd;
        if (point.Y >= scrollbarTop)
        {
            if (Math.Abs(point.X - viewLeft) <= 10)
                _drag = DragAction.ResizeLeft;
            else if (Math.Abs(point.X - viewRight) <= 10)
                _drag = DragAction.ResizeRight;
            else if (viewLeft < point.X && point.X < viewRight)
                _drag = DragAction.Pan;
            else
            {
                // Clicking the track outside the view centres the view there, then drags it.
                var span = _visibleEnd - _visibleStart;
                var (left, right) = HorizontalBounds();
                var center = (Math.Clamp(point.X, left, right) - left) / Math.Max(1, right - left) * _duration;
                _visibleStart = Math.Max(0.0, Math.Min(_duration - span, center - span / 2));
                _visibleEnd = _visibleStart + span;
                _drag = DragAction.Pan;
                _dragStart = _visibleStart;
                _dragEnd = _visibleEnd;
                InvalidateVisual();
            }
        }
        else
        {
            _drag = DragAction.Seek;
            RequestSeek(point.X, final: false);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag == DragAction.None)
        {
            UpdateCursor(point);
            return;
        }

        var (left, right) = HorizontalBounds();
        var delta = (point.X - _dragX) / Math.Max(1, right - left) * _duration;
        var minimum = Math.Min(_duration, Math.Max(0.1, 5.0 / _frameRate));
        switch (_drag)
        {
            case DragAction.ResizeLeft:
                _visibleStart = Math.Max(0.0, Math.Min(_dragEnd - minimum, _dragStart + delta));
                break;
            case DragAction.ResizeRight:
                _visibleEnd = Math.Min(_duration, Math.Max(_dragStart + minimum, _dragEnd + delta));
                break;
            case DragAction.Pan:
                var span = _dragEnd - _dragStart;
                _visibleStart = Math.Max(0.0, Math.Min(_duration - span, _dragStart + delta));
                _visibleEnd = _visibleStart + span;
                break;
            case DragAction.Seek:
                RequestSeek(point.X, final: false);
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragAction.None)
            return;
        var point = e.GetPosition(this);
        if (_drag == DragAction.Seek)
            RequestSeek(point.X, final: true);
        _drag = DragAction.None;
        UpdateCursor(point);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == DragAction.Seek)
            SeekRequested?.Invoke(_position, true);
        _drag = DragAction.None;
    }

    private void RequestSeek(double x, bool final)
    {
        var position = XToTime(x);
        SetPosition(position);
        SeekRequested?.Invoke(position, final);
    }

    private void UpdateCursor(Point point)
    {
        var (scrollbarTop, viewLeft, viewRight) = ScrollbarGeometry();
        if (point.Y >= scrollbarTop && (Math.Abs(point.X - viewLeft) <= 10 || Math.Abs(point.X - viewRight) <= 10))
            Cursor = _resizeCursor ??= new Cursor(StandardCursorType.SizeWestEast);
        else if (point.Y >= scrollbarTop && viewLeft < point.X && point.X < viewRight)
            Cursor = _panCursor ??= new Cursor(StandardCursorType.SizeAll);
        else
            Cursor = _seekCursor ??= new Cursor(StandardCursorType.Hand);
    }

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    private static IBrush Rgb(double r, double g, double b, double a = 1.0) =>
        new SolidColorBrush(Color.FromArgb((byte)(a * 255), (byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
}
