using ClubShell.Agent.Server;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

public sealed class HeartbeatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 10, 15, 30, TimeSpan.Zero);

    [Fact]
    public async Task Unknown_pending_command_is_acked_notFound_and_the_rest_of_the_batch_still_runs()
    {
        Guid pcId = Guid.NewGuid();
        var unknown = new ServerCommandEnvelope(Guid.NewGuid(), T0, "dance", null);
        var unlock = new ServerCommandEnvelope(Guid.NewGuid(), T0, "unlock", null);
        var server = Substitute.For<IServerClient>();
        server.GetCommandsAsync(pcId, Arg.Any<CancellationToken>()).Returns(new ServerCommandsResponse([unknown, unlock]));
        var sink = Substitute.For<IServerCommandSink>();
        sink.HandleAsync(Arg.Any<ServerCommand>(), Arg.Any<CancellationToken>()).Returns(CommandAck.Success());
        server.ServerNow.Returns(T0);

        await HeartbeatService.DrainCommandsAsync(server, sink, NullLogger.Instance, pcId, CancellationToken.None);

        await sink.Received(1).HandleAsync(Arg.Is<ServerCommand>(c => c.Id == unlock.Id && c.Type == ServerCommandType.Unlock), Arg.Any<CancellationToken>());
        await sink.Received(1).HandleAsync(Arg.Any<ServerCommand>(), Arg.Any<CancellationToken>());
        await server.Received(1).AckCommandAsync(pcId, unknown.Id, Arg.Is<CommandAck>(a => !a.Ok && a.Error!.Code == ErrorCode.NotFound), Arg.Any<CancellationToken>());
        await server.Received(1).AckCommandAsync(pcId, unlock.Id, Arg.Is<CommandAck>(a => a.Ok), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pending_command_expiry_is_judged_on_the_server_corrected_clock()
    {
        // The PC clock may lead by hours (local time stored as UTC); ServerNow = local + learned offset.
        Guid pcId = Guid.NewGuid();
        var envelope = new ServerCommandEnvelope(Guid.NewGuid(), T0, "unlock", null, ExpiresAt: T0.AddMinutes(10));
        var server = Substitute.For<IServerClient>();
        server.GetCommandsAsync(pcId, Arg.Any<CancellationToken>()).Returns(new ServerCommandsResponse([envelope]));
        var sink = Substitute.For<IServerCommandSink>();
        sink.HandleAsync(Arg.Any<ServerCommand>(), Arg.Any<CancellationToken>()).Returns(CommandAck.Success());

        server.ServerNow.Returns(T0.AddMinutes(1));
        await HeartbeatService.DrainCommandsAsync(server, sink, NullLogger.Instance, pcId, CancellationToken.None);

        await sink.Received(1).HandleAsync(Arg.Is<ServerCommand>(c => c.Id == envelope.Id), Arg.Any<CancellationToken>());
        await server.Received(1).AckCommandAsync(pcId, envelope.Id, Arg.Is<CommandAck>(a => a.Ok), Arg.Any<CancellationToken>());

        server.ServerNow.Returns(T0.AddMinutes(11));
        await HeartbeatService.DrainCommandsAsync(server, sink, NullLogger.Instance, pcId, CancellationToken.None);

        await sink.Received(1).HandleAsync(Arg.Any<ServerCommand>(), Arg.Any<CancellationToken>());
        await server.Received(1).AckCommandAsync(pcId, envelope.Id, Arg.Is<CommandAck>(a => !a.Ok && a.Error!.Code == ErrorCode.Timeout), Arg.Any<CancellationToken>());
    }
}
