using Microsoft.VisualStudio.Threading;
using Shadowsocks.Controller;
using Shadowsocks.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Shadowsocks.Util.NetUtils
{
    public static class DnsUtil
    {
        public static LRUCache<string, IPAddress> DnsBuffer { get; } = new();

        public static IPAddress? QueryDns(string host)
        {
            var res = RunSync(() => QueryDnsCoreAsync(host));
            Logging.Info(res is null
                    ? $@"DNS query {host} failed."
                    : $@"DNS query {host} answer {res}");
            return res;
        }

        private static async Task<IPAddress?> QueryDnsCoreAsync(string host)
        {
            return host.Contains('.') && Global.GuiConfig.DnsClients.Any(s => s.Enable)
                    ? await QueryAsync(host, Global.GuiConfig.DnsClients)
                    : await QueryDefaultAsync(host);
        }

        private static IPAddress? RunSync(Func<Task<IPAddress?>> taskFactory)
        {
            IPAddress? result = null;
            Exception? exception = null;
            using var completed = new ManualResetEventSlim();

            RunAndSignalAsync().Forget();
            completed.Wait();

            if (exception != null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            return result;

            async Task RunAndSignalAsync()
            {
                try
                {
                    result = await taskFactory();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    completed.Set();
                }
            }
        }

        public static async Task<IPAddress?> QueryDefaultAsync(string host, bool ipv6First = default)
        {
            return await DnsClient.QueryIpAddressDefaultAsync(host, ipv6First, default);
        }

        public static async Task<IPAddress?> QueryAsync(string host, IEnumerable<DnsClient> clients)
        {
            return await clients
                    .Where(client => client.Enable)
                    .Select(s => Observable
                            .FromAsync(ct => s.QueryIpAddressAsync(host, ct))
                            .Where(ip => ip is not null)
                    )
                    .Merge()
                    .FirstOrDefaultAsync();
        }
    }
}
