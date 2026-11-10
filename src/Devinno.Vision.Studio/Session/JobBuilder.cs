using Devinno.Vision.Studio.Model;

namespace Devinno.Vision.Studio.Session;

/// <summary>프로젝트 → 실행할 Job. 검사 순서대로 단계를 쌓는다.</summary>
internal static class JobBuilder
{
    /// <summary>정렬 검사가 만드는 Pose 이름. 뒤 검사들의 ROI 가 이것을 따라간다.</summary>
    public const string PoseName = "part";

    public static VisionJob Build(StudioProject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var job = new VisionJob(string.IsNullOrWhiteSpace(p.Name) ? "studio" : p.Name) { Calibration = p.Calibration };
        foreach (var check in p.Checks)
        {
            var roi = check.Roi;   // 지금 값 (뒤 편집과 무관하게)
            job.Add(check.Build(p), roi: roi.IsEmpty ? null : () => roi, pose: check.MakesPose ? PoseName : null);
        }
        return job;
    }
}
