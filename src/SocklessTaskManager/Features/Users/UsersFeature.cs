using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Users;

/// <summary>
/// The Users page: who is signed in to this computer, each with their sessions' state, sign-in and
/// idle time, and the CPU and memory their processes use; or every account that runs a process,
/// services and Windows itself included. A session can be disconnected or signed out from its
/// details, and its processes opened on the Processes page. Samples only while the page is on screen.
/// </summary>
public sealed class UsersFeature : Feature, ISampled
{
    public UsersFeature(SampleClock clock, Navigator navigator)
    {
        clock.Subscribe(this);
        List = new UserList(navigator);
    }

    public override string Title => "Users";

    public override string IconKey => "IconUsers";

    public UserList List { get; }

    protected override Control CreateView() => new UsersView { DataContext = List };

    void ISampled.Sample() => List.Sample();

    void ISampled.Apply() => List.Apply();
}
