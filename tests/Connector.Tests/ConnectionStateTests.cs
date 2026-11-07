using System.Text.Json;
using Connector.Core;
using Connector.WeChat;
using Xunit;

namespace Connector.Tests;

public sealed class ConnectionStateTests
{
    [Fact]
    public void OldEncryptedStateSchemaCanResumeWithoutLosingEventsOrCursor()
    {
        var json = """{"Account":{"BotToken":"test","BotId":"bot","OwnerId":"owner","BaseUrl":"https://ilinkai.weixin.qq.com/"},"Cursor":"old-cursor","Events":[{"StorageKey":"old","Event":null,"State":2,"ReasonCode":"inserting"}],"VoiceTotal":7}""";
        var account = JsonSerializer.Deserialize<ConnectionState>(json)!;
        var runtime = account.ToRuntime(false, true);
        Assert.Equal("old-cursor", runtime.Cursor); Assert.Single(runtime.Events); Assert.Empty(runtime.Pending);
        Assert.False(runtime.AutoInput); Assert.True(runtime.AutoEnter); Assert.Equal(7, account.VoiceTotal);
    }

    [Fact]
    public void RoundTripKeepsInputAndOutboxInSameSnapshotWithoutDuplicatingBody()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new InboxItem("id", new("bot", "id", "owner", "UNIQUE_PRIVATE_BODY", now, now), EventState.Verified, "done");
        var reply = new ReplyContext("bot", "owner", "ctx", "id", now);
        var runtime = new ReceiptSnapshot("cursor", [item], [new("receipt", "client", reply, "状态回执", now, now)], InputPaused: true);
        var account = new ConnectionState(new("test", "bot", "owner", new("https://ilinkai.weixin.qq.com/")), null, []);
        var json = JsonSerializer.Serialize(account.WithRuntime(runtime));
        Assert.Equal(1, json.Split("UNIQUE_PRIVATE_BODY").Length - 1);
        var restored = JsonSerializer.Deserialize<ConnectionState>(json)!.ToRuntime(true, false);
        Assert.Equal(EventState.Verified, Assert.Single(restored.Events).State);
        Assert.Equal("ctx", Assert.Single(restored.Pending).Reply.ContextToken); Assert.True(restored.InputPaused);
    }
}
