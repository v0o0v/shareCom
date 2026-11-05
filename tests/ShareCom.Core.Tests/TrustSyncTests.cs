using ShareCom.Core.Identity;
using static ShareCom.Core.Tests.NodeTests;

namespace ShareCom.Core.Tests;

public class TrustSyncTests
{
    private static (TestNode Node, TrustSync Trust) Make3(FakeDiscoveryHub hub, string name)
    {
        var n = Make(hub, name);
        return (n, new TrustSync(n.Node, n.Store));
    }

    private static Guid Id(TestNode n) => n.Identity.Id;

    [Fact]
    public async Task 새_PC가_한_PC와만_짝을_맺어도_나머지와_자동으로_짝이_되어_연결된다()
    {
        var hub = new FakeDiscoveryHub();
        var (a, _) = Make3(hub, "A");
        var (b, _) = Make3(hub, "B");
        var (c, _) = Make3(hub, "C");
        await using (a) await using (b) await using (c)
        {
            await PairAndConnect(a, b);
            await PairAndConnect(a, c); // B 와 C 는 직접 짝을 맺지 않는다

            await Until(() => b.Store.Get(Id(c)) is not null && c.Store.Get(Id(b)) is not null, "B·C 가 서로를 소개받음");
            Assert.Equal(c.Identity.Thumbprint, b.Store.Get(Id(c))!.Thumbprint);
            Assert.Equal(b.Identity.Thumbprint, c.Store.Get(Id(b))!.Thumbprint);
            await Until(() => b.Node.IsConnected(Id(c)) && c.Node.IsConnected(Id(b)), "B·C 연결");
        }
    }

    [Fact]
    public async Task 한_PC에서_짝을_풀면_모든_PC에서_풀리고_다시_직접_맺으면_되살아난다()
    {
        var hub = new FakeDiscoveryHub();
        var (a, trustA) = Make3(hub, "A");
        var (b, trustB) = Make3(hub, "B");
        var (c, _) = Make3(hub, "C");
        await using (a) await using (b) await using (c)
        {
            await PairAndConnect(a, b);
            await PairAndConnect(a, c);
            await Until(() => b.Node.IsConnected(Id(c)), "B·C 연결");

            trustB.Unpair(Id(c));
            await Until(() => a.Store.Get(Id(c)) is null, "A 에서도 C 의 짝이 풀림");
            Assert.Null(b.Store.Get(Id(c)));
            Assert.True(a.Store.Revoked.ContainsKey(Id(c)));
            await Until(() => !a.Node.IsConnected(Id(c)) && !b.Node.IsConnected(Id(c)), "C 와의 연결이 끊김");

            // 잠시 지나도 옛 소개로 되살아나지 않는다.
            await Task.Delay(800);
            Assert.Null(a.Store.Get(Id(c)));
            Assert.Null(b.Store.Get(Id(c)));

            // A 가 C 와 다시 직접 짝을 맺으면 B 에게도 다시 소개된다.
            await PairAndConnect(a, c);
            await Until(() => b.Store.Get(Id(c)) is not null, "B 가 C 를 다시 소개받음");
            Assert.False(b.Store.Revoked.ContainsKey(Id(c)));
            await Until(() => b.Node.IsConnected(Id(c)), "B·C 다시 연결");
            _ = trustA;
        }
    }
}
