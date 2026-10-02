using System.Text;
using Microsoft.AspNetCore.Http;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Http;

public class RequestHardeningTests
{
    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("forwarded")]
    [InlineData("CF-Connecting-IP")]
    [InlineData("Via")]
    public void Requests_through_a_proxy_or_tunnel_are_recognised(string header)
    {
        var headers = new HeaderDictionary { [header] = "203.0.113.7" };

        Assert.NotNull(RequestGuard.ProxyHeader(headers));
        Assert.Null(RequestGuard.ProxyHeader(new HeaderDictionary { ["X-OptiCli-Token"] = "t", ["User-Agent"] = "opticli" }));
    }

    /// <summary>A body without Content-Length, like a chunked one, that never ends: reading must stop at the limit.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Served { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            Served += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_body_without_a_length_is_cut_off_at_the_limit()
    {
        var body = new EndlessStream();
        var context = new DefaultHttpContext();
        context.Request.Body = body;

        var refused = await Assert.ThrowsAsync<AgentException>(() => new AgentRequest(context, null).ReadOptionalBodyAsync<MoveRequest>(1024 * 1024));

        Assert.Equal(AgentErrorCodes.Usage, refused.Code);
        Assert.True(body.Served < 2 * 1024 * 1024, $"read {body.Served} bytes");
    }

    [Fact]
    public async Task A_body_within_the_limit_is_read()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"parent":"5"}"""));

        Assert.Equal("5", (await new AgentRequest(context, null).ReadOptionalBodyAsync<MoveRequest>())!.Parent);
    }
}
