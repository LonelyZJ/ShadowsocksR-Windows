using Microsoft.VisualStudio.Threading;
using Shadowsocks.Enums;
using Shadowsocks.Model;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Controller.HttpRequest
{
    public class UpdateNode : HttpRequest
    {
        public const string DefaultUpdateUrl = @"https://raw.githubusercontent.com/HMBSbige/Text_Translation/master/ShadowsocksR/freenodeplain.txt";

        public event EventHandler NewFreeNodeFound;
        public string FreeNodeResult;
        public ServerSubscribe SubscribeTask;
        public bool Notify;

        public void CheckUpdate(Configuration config, ServerSubscribe subscribeTask, bool notify)
        {
            FreeNodeResult = null;
            Notify = notify;
            try
            {
                SubscribeTask = subscribeTask;
                UpdateAsync(config, subscribeTask, notify).Forget();
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
            }
        }

        public virtual async Task<string> CheckUpdateAsync(Configuration config, ServerSubscribe subscribeTask, bool notify, CancellationToken ct)
        {
            var proxy = CreateProxy(config);
            var url = subscribeTask.Url ?? DefaultUpdateUrl;
            var timeout = config.ConnectTimeout * 1000;
            var userAgent = config.ProxyUserAgent;

            ct.ThrowIfCancellationRequested();
            return subscribeTask.ProxyType switch
            {
                HttpRequestProxyType.Auto => await AutoGetAsync(url, proxy, userAgent, timeout),
                HttpRequestProxyType.Direct => await DirectGetAsync(url, userAgent, timeout),
                HttpRequestProxyType.Proxy => await ProxyGetAsync(url, proxy, userAgent, timeout),
                HttpRequestProxyType.SystemSetting => await DefaultGetAsync(url, userAgent, timeout),
                _ => await AutoGetAsync(url, proxy, userAgent, timeout)
            };
        }

        private async Task UpdateAsync(Configuration config, ServerSubscribe subscribeTask, bool notify)
        {
            try
            {
                FreeNodeResult = await CheckUpdateAsync(config, subscribeTask, notify, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logging.Debug(ex.ToString());
            }

            NewFreeNodeFound?.Invoke(this, EventArgs.Empty);
        }
    }
}
