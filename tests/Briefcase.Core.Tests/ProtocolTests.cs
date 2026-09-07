using System.Buffers.Binary;
using ServerAdminControl.Protocol;

namespace Briefcase.Core.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Administration_frame_round_trips_through_partial_reads()
    {
        var request = new ServerAdminRequest(
            ServerAdminProtocol.Version, "request-1", ServerAdminOperations.GetPlayers);
        await using var encoded = new MemoryStream();
        await ServerAdminProtocol.WriteAsync(encoded, request, CancellationToken.None);
        await using var chunks = new ChunkedReadStream(encoded.ToArray(), 2);

        var decoded = await ServerAdminProtocol.ReadAsync<ServerAdminRequest>(
            chunks, CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal(request, decoded);
        Assert.Equal(BriefcaseChannels.Administration, decoded.Channel);
    }

    [Fact]
    public async Task Handshake_frame_round_trips()
    {
        var hello = new ModHandshakeClientHello(
            ModHandshakeProtocol.Version,
            "request-2",
            "client-1",
            "1.2.3",
            new ModHandshakeBuild(0x12345678, 0x01000000),
            [new ModHandshakeMod(
                "sample.mod", "Sample.dll", "Sample", "1.0.0",
                new string('A', 64), true, true, [], "Client")]);
        await using var stream = new MemoryStream();

        await ModHandshakeProtocol.WriteAsync(stream, hello, CancellationToken.None);
        stream.Position = 0;
        var decoded = await ModHandshakeProtocol.ReadAsync<ModHandshakeClientHello>(
            stream, CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal("client-1", decoded.ClientInstanceId);
        Assert.Equal("sample.mod", Assert.Single(decoded.Mods).Id);
        Assert.Equal("12345678-01000000", decoded.GameBuild.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ModHandshakeProtocol.MaximumFrameBytes + 1)]
    public async Task Handshake_rejects_invalid_frame_lengths(int length)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ModHandshakeProtocol.ReadAsync<object>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Protocol_rejects_an_incomplete_header()
    {
        await using var stream = new MemoryStream([1, 2]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await ServerAdminProtocol.ReadAsync<object>(stream, CancellationToken.None));
    }

    [Fact]
    public void Authentication_proof_is_bound_to_every_request_field()
    {
        var salt = Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var nonce = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var key = ServerAdminAuthentication.DeriveKey("secret", salt, 50_000);
        var proof = ServerAdminAuthentication.CreateProof(
            key, ServerAdminProtocol.Version, "request-3", ServerAdminOperations.Status,
            "challenge-1", nonce);

        Assert.True(ServerAdminAuthentication.VerifyProof(
            key, ServerAdminProtocol.Version, "request-3", ServerAdminOperations.Status,
            "challenge-1", nonce, proof));
        Assert.False(ServerAdminAuthentication.VerifyProof(
            key, ServerAdminProtocol.Version, "request-3", ServerAdminOperations.RestartServer,
            "challenge-1", nonce, proof));
        Assert.False(ServerAdminAuthentication.VerifyProof(
            key, ServerAdminProtocol.Version, "other-request", ServerAdminOperations.Status,
            "challenge-1", nonce, proof));
        Assert.False(ServerAdminAuthentication.VerifyProof(
            key, ServerAdminProtocol.Version, "request-3", ServerAdminOperations.Status,
            "challenge-1", nonce, "not-base64"));
    }

    [Fact]
    public void Authentication_rejects_unsafe_derivation_parameters()
    {
        var validSalt = Convert.ToBase64String(new byte[ServerAdminAuthentication.SaltBytes]);
        Assert.Throws<InvalidOperationException>(() =>
            ServerAdminAuthentication.DeriveKey("", validSalt, 50_000));
        Assert.Throws<InvalidDataException>(() =>
            ServerAdminAuthentication.DeriveKey("secret", validSalt, 49_999));
        Assert.Throws<InvalidDataException>(() =>
            ServerAdminAuthentication.DeriveKey("secret", "invalid", 50_000));
        Assert.Throws<InvalidDataException>(() =>
            ServerAdminAuthentication.DeriveKey(
                "secret", Convert.ToBase64String(new byte[3]), 50_000));
    }
}

internal sealed class ChunkedReadStream(byte[] data, int maximumChunkSize) : Stream
{
    private readonly MemoryStream _inner = new(data);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, Math.Min(count, maximumChunkSize));
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkSize)], cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
