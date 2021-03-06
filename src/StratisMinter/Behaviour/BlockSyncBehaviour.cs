using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using NBitcoin.Protocol.Payloads;
using StratisMinter.Base;
using StratisMinter.Services;
using System.Linq;

namespace StratisMinter.Behaviour
{
	/// <summary>
	/// The block sync behaviour is responsible for listening to blocks on the network 
	/// And serving any required functionality to the hub
	/// It will be able to broadcast blocks to the network
	/// </summary>
	public class BlockSyncBehaviour : NodeBehavior
	{
		private readonly BlockSyncHub blockSyncHub;
		private readonly CancellationTokenSource cancellation;

		/// <summary>
		/// Respond to 'getblocks' messages (Default : true)
		/// </summary>
		public bool CanRespondToGetBlocksPayload { get; set; }

		public bool CanRespondToBlockPayload { get; set; }

		public bool CanRespondToHeadersPayload { get; set; }

		public bool CanRespondToInvPayload { get; set; }

		public bool CanRespondeToGetDataPayload { get; set; }

		public BlockSyncBehaviour(BlockSyncHub hub)
		{
			this.blockSyncHub = hub;
			this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(new[] { this.blockSyncHub.Context.CancellationToken });
			this.CanRespondToGetBlocksPayload = true;
			this.CanRespondeToGetDataPayload = true;
			this.CanRespondToHeadersPayload = true;
			this.CanRespondToInvPayload = true;
		}

		protected override void AttachCore()
	    {
			// listen to both state changed events 
			// and message received events
			this.AttachedNode.StateChanged += AttachedNode_StateChanged;
			this.AttachedNode.MessageReceived += AttachedNode_MessageReceived;
	    }

		private void AttachedNode_MessageReceived(Node node, IncomingMessage message)
		{
			//this.blockSyncHub.Logger.LogInformation(
			//	$"msg - {node.Peer.Endpoint} - {message.Message.Payload.GetType().Name} - {message.Message.Payload.Command}");

			var getBlocksPayload = message.Message.Payload as GetBlocksPayload;
			if (this.CanRespondToGetBlocksPayload && getBlocksPayload != null)
				this.RespondToGetBlocksPayload(node, getBlocksPayload);

			var blockPayload = message.Message.Payload as BlockPayload;
			if (this.CanRespondToBlockPayload && blockPayload != null)
				this.RespondToBlockPayload(node, blockPayload);

			var headersPayload = message.Message.Payload as HeadersPayload;
			if (this.CanRespondToHeadersPayload && headersPayload != null)
				this.RespondToHeadersPayload(node, headersPayload);

			var invPayload = message.Message.Payload as InvPayload;
			if (this.CanRespondToInvPayload && invPayload != null)
				this.RespondToInvPayload(node, invPayload);

			var getDataPayload = message.Message.Payload as GetDataPayload;
			if (this.CanRespondeToGetDataPayload && getDataPayload != null)
				this.RespondToGetDataPayload(node, getDataPayload);

			var notFoundPayload = message.Message.Payload as NotFoundPayload;
			if (notFoundPayload != null)
				this.RespondToNotFoundPayload(node, notFoundPayload);

			var rejectPayload = message.Message.Payload as RejectPayload;
			if (rejectPayload != null)
				this.RespondToRejectPayload(node, rejectPayload);

		}

		private void RespondToNotFoundPayload(Node node, NotFoundPayload notFound)
		{
			
		}

		private void RespondToRejectPayload(Node node, RejectPayload rejectPayload)
		{

		}


		private void RespondToGetDataPayload(Node node, GetDataPayload getDataPayload)
		{
			this.blockSyncHub.GetDataItems.TryAdd(new HubGetDataItem
			{
				Behaviour = this,
				Payload = getDataPayload,
				Node = this.AttachedNode
			});

			RequestCounter requestCounter;
			foreach (var vector in getDataPayload.Inventory)
				if (this.blockSyncHub.RequestCount.TryGetValue(vector.Hash, out requestCounter))
					Interlocked.Increment(ref requestCounter.Count);
		}

		private void RespondToGetBlocksPayload(Node node, GetBlocksPayload getBlocksPayload)
		{
			// ideally this would go on in a queue running in its own thread
			// and serves getblock requests this can also be throttled 
			// if our node is too busy we just send a reject message

			// push the GetBlocksPayload to the hug for processing
		}

		private void RespondToBlockPayload(Node node, BlockPayload blockPayload)
		{
			this.blockSyncHub.ReceiveBlocks.Add(new HubReceiveBlockItem {Payload = blockPayload, Block = blockPayload.Object, Behaviour = this});

			RequestCounter requestCounter;
			if (this.blockSyncHub.RequestCount.TryGetValue(blockPayload.Object.GetHash(), out requestCounter))
				Interlocked.Increment(ref requestCounter.Count);
		}

		private void RespondToHeadersPayload(Node node, HeadersPayload headersPayload)
		{
			var message = this.CreateGetDataPayload(headersPayload.Headers.Select(item => item.GetHash()));

			if (message.Inventory.Any())
				node.SendMessage(message);
		}

		private void RespondToInvPayload(Node node, InvPayload invPayload)
		{
			var message = this.CreateGetDataPayload(invPayload.Inventory.Where(inv => inv.Type == InventoryType.MSG_BLOCK).Select(item => item.Hash));

			if (message.Inventory.Any())
				node.SendMessage(message);

			RequestCounter requestCounter;
			foreach (var vector in invPayload.Inventory)
				if (this.blockSyncHub.RequestCount.TryGetValue(vector.Hash, out requestCounter))
					Interlocked.Increment(ref requestCounter.Count);
		}

		private GetDataPayload CreateGetDataPayload(IEnumerable<uint256> hashes)
		{
			var message = new GetDataPayload();
			foreach (var hash in hashes)
			{
				if (!this.blockSyncHub.ChainIndex.Contains(hash))
				{
					message.Inventory.Add(new InventoryVector()
					{
						Type = InventoryType.MSG_BLOCK,
						Hash = hash
					});
				}
			}

			return message;
		}

		private void AttachedNode_StateChanged(Node node, NodeState oldState)
		{
			switch (node.State)
			{
				case NodeState.HandShaked:
				{
					// add the behaviour to the hub
					this.blockSyncHub.Behaviours.TryAdd(this, this.AttachedNode);
					this.AskBlocksIfBehind();
					break;
				}
				case NodeState.Failed:
				case NodeState.Disconnecting:
				case NodeState.Offline:
				{
					// remove the behaviour
					Node outnode;
					this.blockSyncHub.Behaviours.TryRemove(this, out outnode);
					break;
				}
			}
		}

		private void AskBlocksIfBehind()
		{
			if(this.blockSyncHub.Context.DownloadMode)
				return;

			if (this.AttachedNode.PeerVersion.StartHeight - this.blockSyncHub.ChainIndex.Height > 10)
			{
				var message = new GetBlocksPayload()
				{
					BlockLocators = this.blockSyncHub.ChainIndex.Tip.GetLocator()
				};
				this.AttachedNode.SendMessage(message);
			}
		}

		protected override void DetachCore()
	    {
			this.AttachedNode.StateChanged -= AttachedNode_StateChanged;
		}

		public override object Clone()
		{
			return new BlockSyncBehaviour(this.blockSyncHub)
			{
				CanRespondToBlockPayload = this.CanRespondToBlockPayload,
				CanRespondToGetBlocksPayload = this.CanRespondToGetBlocksPayload,
				CanRespondToHeadersPayload = this.CanRespondToHeadersPayload,
				CanRespondToInvPayload = this.CanRespondToInvPayload
			};
		}
    }
}
