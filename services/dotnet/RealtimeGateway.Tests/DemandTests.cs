using RealtimeGateway;
using Xunit;

namespace RealtimeGateway.Tests;

public class DemandTests
{
    static List<(string Camera, int Live, int Frames)> Due(DemandTracker t, long now) { var l = new List<(string, int, int)>(); t.CollectDue(now, l); return l; }

    [Fact] public void Changes_are_debounced_per_camera_and_coalesced()
    {
        var t = new DemandTracker();
        t.Add("a", false, 0); t.Add("a", false, 300); t.Add("a", true, 400);
        Assert.Empty(Due(t, 999));
        Assert.Equal(new[] { ("a", 2, 1) }, Due(t, 1000));
        Assert.Empty(Due(t, 5000));                       // not dirty any more
    }

    [Fact] public void Drop_to_zero_is_published_as_zero_then_forgotten_and_not_republished()
    {
        var t = new DemandTracker();
        t.Add("a", false, 0); Due(t, 1000);
        t.Remove("a", false, 2000);
        Assert.Equal(new[] { ("a", 0, 0) }, Due(t, 3000));
        var nz = new List<(string, int, int)>(); t.CollectNonZero(nz);
        Assert.Empty(nz);
    }

    [Fact] public void Republish_lists_all_nonzero_cameras_and_gauges_track_kinds()
    {
        var t = new DemandTracker();
        t.Add("a", false, 0); t.Add("b", true, 0); t.Add("c", false, 0); t.Remove("c", false, 0);
        var nz = new List<(string Camera, int Live, int Frames)>(); t.CollectNonZero(nz);
        Assert.Equal(new[] { "a", "b" }, nz.Select(x => x.Camera).OrderBy(x => x));
        Assert.Equal(1, t.WatchedLive); Assert.Equal(1, t.WatchedFrames);
    }

    [Fact] public void Removing_more_than_added_never_goes_negative()
    {
        var t = new DemandTracker();
        t.Remove("x", false, 0); t.Add("x", false, 0); t.Remove("x", false, 0); t.Remove("x", false, 0);
        Assert.Equal(0, t.Live("x")); Assert.Equal(0, t.WatchedLive);
    }

    [Fact] public void LiveState_counts_both_kinds_and_payload_matches_contract()
    {
        var s = new LiveState();
        s.AddSub("a"); s.AddSub("a"); s.AddFrameWatch("a");
        Assert.Equal(2, s.Demand.Live("a")); Assert.Equal(1, s.Demand.Frames("a"));
        s.RemoveSub("a"); s.RemoveSub("a"); s.RemoveFrameWatch("a");
        Assert.Equal(0, s.Demand.Live("a") + s.Demand.Frames("a"));
        Assert.Equal("{\"camera_id\":\"a\",\"live_viewers\":2,\"frame_viewers\":0,\"ts\":5}",
            System.Text.Encoding.UTF8.GetString(DemandPublisher.Payload("a", 2, 0, 5)));
    }
}
