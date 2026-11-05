using ShareCom.Core.Net;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Layout;

/// <summary>
/// 모든 PC가 함께 쓰는 화면 배치를 맞춘다. 연결되면 서로 배치를 보내고 더 새 판을 고른다.
/// 각 PC는 자기 항목(이름·화면)만 넣고 고친다. 고른 판에 내 항목이 없거나 내 화면과 다르면 고쳐서 새 판으로 퍼뜨린다.
/// 짝이 풀린 PC 항목은 지운다. 배치 편집기에서 바꾼 위치도 새 판으로 퍼뜨린다.
/// </summary>
public sealed class LayoutSync
{
    private readonly Guid _self;
    private readonly IPeerMessenger _messenger;
    private readonly Func<Guid, NeighborSide?> _sideHint;
    private readonly Func<Guid, bool> _isRevoked;
    private readonly object _gate = new();
    private LayoutDoc _doc;
    private string _name = "";
    private PcScreen? _screen;

    /// <param name="sideHint">짝 맺을 때 정한 방향(그 PC가 내 어느 쪽인지). 배치에 처음 들어갈 때 그 PC 옆에 놓는다.</param>
    /// <param name="isRevoked">짝을 푼 PC인지.</param>
    public LayoutSync(Guid self, IPeerMessenger messenger, Func<Guid, NeighborSide?> sideHint, Func<Guid, bool> isRevoked, LayoutDoc? saved = null)
    {
        _self = self;
        _messenger = messenger;
        _sideHint = sideHint;
        _isRevoked = isRevoked;
        _doc = saved ?? LayoutDoc.Empty;
    }

    public LayoutDoc Doc { get { lock (_gate) return _doc; } }

    /// <summary>배치가 바뀌었다(저장·입력 라우터·편집기 갱신용). 잠금 밖에서 부른다.</summary>
    public event Action<LayoutDoc>? Changed;

    /// <summary>내 이름과 화면(시작할 때, 모니터 구성이 바뀔 때).</summary>
    public void SetSelf(string name, PcScreen screen)
    {
        LayoutDoc? publish;
        lock (_gate)
        {
            _name = name;
            _screen = screen;
            publish = FixLocked() ? _doc : null;
        }
        if (publish is not null) Publish(publish, except: null);
    }

    public void OnConnected(Guid peer) => _messenger.Send(peer, new LayoutMessage(Doc));

    public void OnMessage(Guid from, Message message)
    {
        if (message is not LayoutMessage m) return;
        LayoutDoc publish;
        bool fixedUp;
        lock (_gate)
        {
            if (!m.Doc.IsNewerThan(_doc)) return;
            _doc = m.Doc;
            fixedUp = FixLocked();
            publish = _doc;
        }
        // 내가 고쳤으면 보낸 PC에도 새 판을 보낸다.
        Publish(publish, except: fixedUp ? null : from);
    }

    /// <summary>배치 편집기에서 정한 위치. 없는 PC는 그대로 둔다.</summary>
    public void ApplyPositions(IReadOnlyDictionary<Guid, (int X, int Y)> positions)
    {
        LayoutDoc publish;
        lock (_gate)
        {
            var entries = _doc.Entries.Select(e => positions.TryGetValue(e.Id, out var p) ? e with { X = p.X, Y = p.Y } : e).ToArray();
            _doc = new LayoutDoc(_doc.Version + 1, _self, entries);
            FixLocked(bump: false);
            publish = _doc;
        }
        Publish(publish, except: null);
    }

    /// <summary>짝 목록이 바뀐 뒤 부른다(짝을 푼 PC 항목을 지운다).</summary>
    public void Refresh()
    {
        LayoutDoc? publish;
        lock (_gate) publish = FixLocked() ? _doc : null;
        if (publish is not null) Publish(publish, except: null);
    }

    /// <summary>내 항목을 맞추고 짝을 푼 PC를 지운다. 바뀌었으면 판을 올리고 true.</summary>
    private bool FixLocked(bool bump = true)
    {
        var entries = _doc.Entries.Where(e => e.Id == _self || !_isRevoked(e.Id)).ToList();
        bool changed = entries.Count != _doc.Entries.Length;

        if (_screen is { } screen)
        {
            int index = entries.FindIndex(e => e.Id == _self);
            if (index < 0)
            {
                var (w, h) = DeskLayout.LogicalSize(screen);
                var others = entries.Select(DeskLayout.BoxOf).ToList();
                (DeskLayout.Box, NeighborSide)? hint = null;
                foreach (var e in entries)
                    if (_sideHint(e.Id) is { } side) { hint = (DeskLayout.BoxOf(e), side); break; }
                var (x, y) = DeskLayout.AutoPlace(others, w, h, hint);
                entries.Add(new LayoutEntry(_self, _name, x, y, screen));
                changed = true;
            }
            else if (entries[index].Name != _name || !entries[index].Screen.SameAs(screen))
            {
                entries[index] = entries[index] with { Name = _name, Screen = screen };
                changed = true;
            }
        }

        if (!changed) return false;
        _doc = new LayoutDoc(bump ? _doc.Version + 1 : _doc.Version, _self, entries.ToArray());
        return true;
    }

    private void Publish(LayoutDoc doc, Guid? except)
    {
        Changed?.Invoke(doc);
        var message = new LayoutMessage(doc);
        foreach (var peer in _messenger.ConnectedPeers)
            if (peer != except) _messenger.Send(peer, message);
    }
}
