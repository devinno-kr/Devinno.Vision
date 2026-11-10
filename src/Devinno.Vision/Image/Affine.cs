using OpenCvSharp;

namespace Devinno.Vision;

// 2×3 affine: x' = A·x + B·y + C, y' = D·x + E·y + F. 파생 이미지 좌표 → 원본(frame) 좌표 (docs/02 좌표 추적).
// 공개하지 않는다 — 공개 API 는 VisionImage.ToSource 하나.
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 0, 1, 0);

    public static Affine Translate(double x, double y) => new(1, 0, x, 0, 1, y);

    public Point2d Map(Point2d p) => new(A * p.X + B * p.Y + C, D * p.X + E * p.Y + F);

    // 방향(벡터)만 변환 — 이동 없음
    public Point2d MapVector(Point2d v) => new(A * v.X + B * v.Y, D * v.X + E * v.Y);

    // 먼저 this, 그다음 outer (outer ∘ this). 파생 이미지: childToParent.Then(parent.ToSource)
    public Affine Then(Affine outer) => new(
        outer.A * A + outer.B * D, outer.A * B + outer.B * E, outer.A * C + outer.B * F + outer.C,
        outer.D * A + outer.E * D, outer.D * B + outer.E * E, outer.D * C + outer.E * F + outer.F);

    public Affine Invert()
    {
        var det = A * E - B * D;
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException("The coordinate transform cannot be inverted.");
        var ia = E / det; var ib = -B / det; var id = -D / det; var ie = A / det;
        return new(ia, ib, -(ia * C + ib * F), id, ie, -(id * C + ie * F));
    }

    public bool IsIdentity => this == Identity;
}
