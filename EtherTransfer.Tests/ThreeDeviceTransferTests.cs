using System.Net;
using System.Net.Sockets;
using EtherTransfer.Core.Models;
using EtherTransfer.Services;
using EtherTransfer.Transfer;

namespace EtherTransfer.Tests;
public class ThreeDeviceTransferTests
{
    [Test]
    public async Task AllSixDirectionsTransferWithThreeOnlineServices()
    {
        string root=Path.Combine(Path.GetTempPath(),"EtherThreePeers-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var peers=Enumerable.Range(0,3).Select(i=>new TransferService("peer"+i,0)).ToArray();
        try
        {
            foreach(var peer in peers)peer.Start();
            byte[] bytes=Enumerable.Range(0,65539).Select(i=>(byte)(i%251)).ToArray();
            string source=Path.Combine(root,"source.bin");await File.WriteAllBytesAsync(source,bytes);
            for(int from=0;from<3;from++)for(int to=0;to<3;to++)
            {
                if(from==to)continue;
                string destination=Path.Combine(root,$"{from}-to-{to}");Directory.CreateDirectory(destination);
                var finished=new TaskCompletionSource<TransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                peers[to].OnIncomingTransfer=(_,_)=>Task.FromResult((true,destination,CancellationToken.None));
                EventHandler<TransferResult> handler=(_,result)=>finished.TrySetResult(result);
                peers[to].TransferFinished+=handler;
                var session=new TransferSession { PayloadFileCount=1 };
                session.AddFiles([new FileSelectionItem { AbsolutePath=source,RelativePath="内容.bin",RootName="内容.bin",Size=bytes.Length }]);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await peers[from].TransmitSessionAsync("127.0.0.1",peers[to].TcpPort,session,timeout.Token);
                var result=await finished.Task.WaitAsync(timeout.Token);
                Assert.That(result.Success,Is.True,$"{from}->{to}: {result.ErrorMessage}");
                Assert.That(await File.ReadAllBytesAsync(Path.Combine(destination,"内容.bin")),Is.EqualTo(bytes));
                peers[to].TransferFinished-=handler;
                while(peers[to].IsBusy)await Task.Delay(1,timeout.Token);
            }
        }
        finally { foreach(var peer in peers)peer.Dispose();Directory.Delete(root,true); }
    }
    [Test]
    public async Task ThirdPeerGetsBusyResponseWithoutReplacingFirstConsentOrPublishingItsResult()
    {
        using var service=new TransferService("receiver",0);service.Start();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent=new TaskCompletionSource<(bool,string,CancellationToken)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished=new TaskCompletionSource<TransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int prompts=0,results=0;
        service.OnIncomingTransfer=async(_,ct)=> { Interlocked.Increment(ref prompts);entered.TrySetResult();return await consent.Task.WaitAsync(ct); };
        service.TransferFinished+=(_,result)=> { Interlocked.Increment(ref results);finished.TrySetResult(result); };
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first=new TcpClient();await first.ConnectAsync(IPAddress.Loopback,service.TcpPort,timeout.Token);
        await ProtocolHelper.SendMessageAsync(first.GetStream(),new TransferRequestMessage { SenderName="peer-a" },timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        using var third=new TcpClient();await third.ConnectAsync(IPAddress.Loopback,service.TcpPort,timeout.Token);
        await ProtocolHelper.SendMessageAsync(third.GetStream(),new TransferRequestMessage { SenderName="peer-c" },timeout.Token);
        var response=await ProtocolHelper.ReceiveMessageAsync<TransferResponseMessage>(third.GetStream(),timeout.Token);
        Assert.That(response!.Accepted,Is.False);Assert.That(response.Reason,Does.Contain("另一项传输"));
        Assert.That(prompts,Is.EqualTo(1));Assert.That(results,Is.Zero);Assert.That(service.IsBusy,Is.True);
        consent.TrySetResult((false,"",CancellationToken.None));
        await finished.Task.WaitAsync(timeout.Token);
        Assert.That(results,Is.EqualTo(1));
    }
}
