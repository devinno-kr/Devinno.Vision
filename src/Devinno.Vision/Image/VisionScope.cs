namespace Devinno.Vision;

// 실행 한 번의 이미지 범위. 파생 이미지는 원본의 범위를 물려받는다 — AsyncLocal 을 쓰지 않는다.
internal sealed class VisionScope(string job)
{
    private readonly object _gate = new();
    private readonly List<VisionImage> _items = [];
    private bool _closed;

    public string Job { get; } = job;

    // 지금 실행 중인 단계 이름 — 단계는 순서대로 돌므로 하나다
    public string? CurrentStep { get; set; }

    public void Register(VisionImage image)
    {
        lock (_gate)
        {
            if (_closed)
                throw new InvalidOperationException($"The run of '{Job}' has already ended; images can no longer be created from its images. Call Keep() inside the step to use an image outside the run.");
            image.CreatedInStep = CurrentStep;
            _items.Add(image);
        }
    }

    public void Remove(VisionImage image)
    {
        lock (_gate)
            _items.Remove(image);
    }

    // 그 단계에서 만든 마지막 (해제 안 된) 이미지를 범위에서 꺼낸다 — 결과가 소유 (단계별 중간 이미지 보기)
    public VisionImage? TakeLast(string step)
    {
        lock (_gate)
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var img = _items[i];
                if (img.CreatedInStep == step && !img.IsDisposed)
                {
                    _items.RemoveAt(i);
                    img.DetachScope();
                    return img;
                }
            }
            return null;
        }
    }

    public void Close()
    {
        VisionImage[] items;
        lock (_gate)
        {
            _closed = true;
            items = [.. _items];
            _items.Clear();
        }
        foreach (var img in items)
            img.Release(Job);
    }
}
