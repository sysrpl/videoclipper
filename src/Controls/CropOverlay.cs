using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using videoclipper.Models;
using videoclipper.Services;

namespace videoclipper.Controls;

/// <summary>
/// A crop-rectangle editor drawn over the video preview. Everything outside the rectangle is
/// dimmed. Drag anywhere to draw a new rectangle, drag an edge or corner handle to resize it, or
/// drag the round centre handle to move it without resizing. Coordinates are source pixels; the
/// preview is assumed to show the video letterboxed (Stretch="Uniform") in this control's bounds.
/// </summary>
public sealed class CropOverlay : Control
{
    private const double HandleRadius = 9.0;

    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
    private static readonly IPen OutlinePen = new Pen(new SolidColorBrush(Color.FromArgb(230, 64, 171, 255)), 1.5);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromArgb(71, 255, 255, 255)), 1);
    private static readonly IBrush HandleFill = new SolidColorBrush(Color.FromRgb(242, 247, 255));
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromRgb(41, 102, 158)), 1);
    private static readonly IBrush MoveHandleFill = new SolidColorBrush(Color.FromArgb(217, 26, 92, 153));
    private static readonly IPen MoveHandlePen = new Pen(new SolidColorBrush(Color.FromRgb(242, 247, 255)), 1.5);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));

    private static readonly (string Name, StandardCursorType Cursor)[] HandleCursors =
    [
        ("nw", StandardCursorType.TopLeftCorner), ("n", StandardCursorType.TopSide),
        ("ne", StandardCursorType.TopRightCorner), ("w", StandardCursorType.LeftSide),
        ("e", StandardCursorType.RightSide), ("sw", StandardCursorType.BottomLeftCorner),
        ("s", StandardCursorType.BottomSide), ("se", StandardCursorType.BottomRightCorner),
        ("move", StandardCursorType.SizeAll), ("new", StandardCursorType.Cross),
    ];

    private readonly Dictionary<string, Cursor> _cursors = [];
    private int _videoWidth;
    private int _videoHeight;
    private CropRect _crop;
    private string? _drag;
    private Point _pressPoint;
    private CropRect _pressCrop;

    public CropOverlay()
    {
        // Focusable, so Left and Right still step frames while the overlay covers the preview.
        Focusable = true;
    }

    /// <summary>Raised while the user draws, resizes or moves the rectangle.</summary>
    public event Action<CropRect>? CropChanged;

    public void SetVideoSize(int width, int height)
    {
        _videoWidth = width;
        _videoHeight = height;
        _crop = new CropRect(0, 0, width, height);
        InvalidateVisual();
    }

    /// <summary>Shows a rectangle chosen elsewhere, without reporting it back.</summary>
    public void SetCrop(CropRect crop)
    {
        _crop = crop;
        InvalidateVisual();
    }

    /// <summary>The letterboxed video area, as an offset and a scale factor.</summary>
    private (double OffsetX, double OffsetY, double Scale) View()
    {
        if (_videoWidth < 1 || _videoHeight < 1)
            return (0.0, 0.0, 1.0);
        var scale = Math.Min(Bounds.Width / _videoWidth, Bounds.Height / _videoHeight);
        return ((Bounds.Width - _videoWidth * scale) / 2.0, (Bounds.Height - _videoHeight * scale) / 2.0, scale);
    }

    private Point ToControl(double x, double y)
    {
        var (offsetX, offsetY, scale) = View();
        return new Point(offsetX + x * scale, offsetY + y * scale);
    }

    private Point ToVideo(Point point)
    {
        var (offsetX, offsetY, scale) = View();
        scale = Math.Max(0.000001, scale);
        return new Point((point.X - offsetX) / scale, (point.Y - offsetY) / scale);
    }

    private Rect RectangleOnScreen() =>
        new(ToControl(_crop.X, _crop.Y), ToControl(_crop.X + _crop.Width, _crop.Y + _crop.Height));

    private static IEnumerable<(string Name, Point Point)> Handles(Rect r)
    {
        yield return ("nw", r.TopLeft);
        yield return ("n", new Point(r.Center.X, r.Top));
        yield return ("ne", r.TopRight);
        yield return ("w", new Point(r.Left, r.Center.Y));
        yield return ("e", new Point(r.Right, r.Center.Y));
        yield return ("sw", r.BottomLeft);
        yield return ("s", new Point(r.Center.X, r.Bottom));
        yield return ("se", r.BottomRight);
    }

    public override void Render(DrawingContext context)
    {
        // Transparent, not null, so the pointer is caught everywhere.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_videoWidth < 1 || _crop.Width < 1)
            return;

        var (offsetX, offsetY, scale) = View();
        var frameRight = offsetX + _videoWidth * scale;
        var frameBottom = offsetY + _videoHeight * scale;
        var r = RectangleOnScreen();

        // Dim the four bands around the selection.
        foreach (var band in new[]
                 {
                     new Rect(offsetX, offsetY, frameRight - offsetX, r.Top - offsetY),
                     new Rect(offsetX, r.Bottom, frameRight - offsetX, frameBottom - r.Bottom),
                     new Rect(offsetX, r.Top, r.Left - offsetX, r.Height),
                     new Rect(r.Right, r.Top, frameRight - r.Right, r.Height),
                 })
        {
            if (band.Width > 0 && band.Height > 0)
                context.FillRectangle(DimBrush, band);
        }

        context.DrawRectangle(null, OutlinePen, r);

        for (var step = 1; step <= 2; step++)
        {
            var guideX = r.Left + r.Width * step / 3.0;
            var guideY = r.Top + r.Height * step / 3.0;
            context.DrawLine(GuidePen, new Point(guideX, r.Top), new Point(guideX, r.Bottom));
            context.DrawLine(GuidePen, new Point(r.Left, guideY), new Point(r.Right, guideY));
        }

        foreach (var (_, handle) in Handles(r))
            context.DrawRectangle(HandleFill, HandlePen, new Rect(handle.X - 4, handle.Y - 4, 8, 8));

        // A visible centre grip makes moving distinct from drawing or resizing.
        var center = r.Center;
        context.DrawEllipse(MoveHandleFill, null, center, 10, 10);
        context.DrawLine(MoveHandlePen, new Point(center.X - 5, center.Y), new Point(center.X + 5, center.Y));
        context.DrawLine(MoveHandlePen, new Point(center.X, center.Y - 5), new Point(center.X, center.Y + 5));

        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), weight: FontWeight.Bold);
        var label = new FormattedText($"{_crop.Width} × {_crop.Height}", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 12, HandleFill);
        var textX = Math.Min(Math.Max(r.Left + 4, offsetX + 2), frameRight - label.Width - 6);
        var textY = r.Top - 6 - label.Height > offsetY + 2 ? r.Top - 6 - label.Height : r.Bottom + 4;
        context.FillRectangle(LabelBackground, new Rect(textX - 3, textY - 1, label.Width + 6, label.Height + 2));
        context.DrawText(label, new Point(textX, textY));
    }

    private string HitTest(Point point)
    {
        var r = RectangleOnScreen();
        var center = r.Center;
        if (Math.Abs(point.X - center.X) <= HandleRadius + 3 && Math.Abs(point.Y - center.Y) <= HandleRadius + 3)
            return "move";
        foreach (var (name, handle) in Handles(r))
        {
            if (Math.Abs(point.X - handle.X) <= HandleRadius && Math.Abs(point.Y - handle.Y) <= HandleRadius)
                return name;
        }
        return r.Left < point.X && point.X < r.Right && r.Top < point.Y && point.Y < r.Bottom ? "move" : "new";
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _videoWidth < 1)
            return;
        Focus();
        var point = e.GetPosition(this);
        _drag = HitTest(point);
        _pressPoint = ToVideo(point);
        _pressCrop = _crop;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag is null)
        {
            UpdateCursor(point);
            return;
        }

        var video = ToVideo(point);
        var (x, y, width, height) = (_pressCrop.X, _pressCrop.Y, _pressCrop.Width, _pressCrop.Height);
        double newX, newY, newWidth, newHeight;
        if (_drag == "new")
        {
            newX = Math.Min(_pressPoint.X, video.X);
            newY = Math.Min(_pressPoint.Y, video.Y);
            newWidth = Math.Abs(video.X - _pressPoint.X);
            newHeight = Math.Abs(video.Y - _pressPoint.Y);
        }
        else if (_drag == "move")
        {
            newX = Math.Max(0.0, Math.Min(_videoWidth - width, x + video.X - _pressPoint.X));
            newY = Math.Max(0.0, Math.Min(_videoHeight - height, y + video.Y - _pressPoint.Y));
            newWidth = width;
            newHeight = height;
        }
        else
        {
            double left = x, top = y, right = x + width, bottom = y + height;
            if (_drag.Contains('w'))
                left = video.X;
            if (_drag.Contains('e'))
                right = video.X;
            if (_drag.Contains('n'))
                top = video.Y;
            if (_drag.Contains('s'))
                bottom = video.Y;
            newX = Math.Min(left, right);
            newY = Math.Min(top, bottom);
            newWidth = Math.Abs(right - left);
            newHeight = Math.Abs(bottom - top);
        }
        Apply(newX, newY, newWidth, newHeight);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null)
            return;
        FinishDrag();
        UpdateCursor(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is not null)
            FinishDrag();
    }

    private void FinishDrag()
    {
        // Treat a stray click or a sliver of a drag as no change at all.
        if (_crop.Width < VideoCore.MinCropSize || _crop.Height < VideoCore.MinCropSize)
            Apply(_pressCrop.X, _pressCrop.Y, _pressCrop.Width, _pressCrop.Height);
        _drag = null;
    }

    private void Apply(double x, double y, double width, double height)
    {
        x = Math.Clamp(x, 0.0, _videoWidth);
        y = Math.Clamp(y, 0.0, _videoHeight);
        width = Math.Max(0.0, Math.Min(_videoWidth - x, width));
        height = Math.Max(0.0, Math.Min(_videoHeight - y, height));
        _crop = new CropRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(width), (int)Math.Round(height));
        InvalidateVisual();
        CropChanged?.Invoke(_crop);
    }

    private void UpdateCursor(Point point)
    {
        var action = HitTest(point);
        if (!_cursors.TryGetValue(action, out var cursor))
        {
            cursor = new Cursor(HandleCursors.First(h => h.Name == action).Cursor);
            _cursors[action] = cursor;
        }
        Cursor = cursor;
    }
}
