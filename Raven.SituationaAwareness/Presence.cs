using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.ServiceModel;
using System.ServiceModel.Discovery;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;

namespace Raven.SituationaAwareness
{
	public class Presence : IDisposable
	{
		private readonly TimeSpan heartbeat;
		private Timer timer;
		private readonly ServiceHost serviceHost;
		private readonly Uri myAddress;
		private readonly ConcurrentDictionary<Uri, IDictionary<string,string>> topologyState = new ConcurrentDictionary<Uri, IDictionary<string, string>>();

		public event EventHandler<NodeMetadata> TopologyChanged = delegate { };

		public Presence(IDictionary<string, string> nodeMetadata): this(nodeMetadata, TimeSpan.FromMinutes(5))
		{
			
		}

		public Presence(IDictionary<string, string> nodeMetadata, TimeSpan heartbeat)
		{
			this.heartbeat = heartbeat;
			serviceHost = new ServiceHost(new NodeStateService(nodeMetadata));
			try
			{
				serviceHost.Description.Behaviors.Add(new ServiceDiscoveryBehavior());
				serviceHost.AddServiceEndpoint(new UdpDiscoveryEndpoint());
				myAddress = new UriBuilder("net.tcp", Environment.MachineName, GetAutoPort(), "/Raven.SituationaAwareness/NodeState").Uri;
				serviceHost.AddServiceEndpoint(typeof (INodeStateService), new NetTcpBinding(SecurityMode.None),
				                               myAddress);
			}
			catch
			{
				Dispose();
				throw;
			}
		}

		public void Start()
		{
			serviceHost.Open();
			FindSiblingNodes(serviceHost);
			timer = new Timer(FindSiblingNodes, serviceHost, heartbeat, heartbeat);
		}

		private static int GetAutoPort()
		{
			var globalProperties = IPGlobalProperties.GetIPGlobalProperties();
			var activeTcpListeners = globalProperties.GetActiveTcpListeners();
			const int portRangeStart = 17232;
			for (int i = portRangeStart; i < portRangeStart + 2048; i++)
			{
				var port = i;
				if (activeTcpListeners.All(x => x.Port != port))
					return port;
			}
			throw new InvalidOperationException(
				"After scanning over 2,000 ports, couldn't find one that was open! What is going on in this machine?!");
		}

		private void FindSiblingNodes(object state)
		{
			var discoveryClient = new DiscoveryClient(new UdpDiscoveryEndpoint());
			discoveryClient.FindProgressChanged+=DiscoveryClientOnFindProgressChanged;
			discoveryClient.FindCompleted+=DiscoveryClientOnFindCompleted;
			discoveryClient.FindAsync(new FindCriteria(typeof (INodeStateService)), state);

		}

		private void DiscoveryClientOnFindProgressChanged(object sender, FindProgressChangedEventArgs findProgressChangedEventArgs)
		{
			var endpointDiscoveryMetadata = findProgressChangedEventArgs.EndpointDiscoveryMetadata;
			var listenUri = endpointDiscoveryMetadata.ListenUris.First();
			if (listenUri == myAddress)
				return;

			if (topologyState.ContainsKey(listenUri))
				return; // we already know that one

			var nodeStateServiceAsync = ChannelFactory<INodeStateServiceAsync>.CreateChannel(new NetTcpBinding(SecurityMode.None),
			                                                                                 new EndpointAddress(listenUri));

			Task.Factory.FromAsync<IDictionary<string, string>>(nodeStateServiceAsync.BeginGetMetadata, nodeStateServiceAsync.EndGetMetadata, null)
				.ContinueWith(task =>
				{
					if (task.Exception != null)
						return; // not interested in this one, it just failed

					topologyState.TryAdd(listenUri, task.Result);
					CloseWcf(nodeStateServiceAsync);
					TopologyChanged(this, new NodeMetadata
					{
						ChangeType = TopologyChangeType.Discovered,
						Metadata = task.Result,
						Uri = listenUri
					});
				});
		}

		private static void CloseWcf(object item)
		{
			if (item == null)
				return;

			var stateServiceAsync = (ICommunicationObject)item;
			try
			{
				stateServiceAsync.Close();
			}
			catch
			{
				stateServiceAsync.Abort();
			}
		}

		private static void DiscoveryClientOnFindCompleted(object sender, FindCompletedEventArgs findCompletedEventArgs)
		{
			var disposable = sender as IDisposable;
			if(disposable!=null)
				disposable.Dispose();
		}

		public void Dispose()
		{
			if (timer != null)
				timer.Dispose();
			CloseWcf(serviceHost);
		}
	}

	public class NodeMetadata : EventArgs
	{
		public TopologyChangeType ChangeType { get; set; }
		public IDictionary<string, string> Metadata { get; set; }
		public Uri Uri { get; set; } 
	}

	public enum TopologyChangeType
	{
		Discovered,
		Gone
	}
}