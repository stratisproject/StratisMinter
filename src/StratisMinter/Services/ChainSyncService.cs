﻿using System.IO;
using nStratis;
using nStratis.BitcoinCore;

namespace StratisMinter.Services
{

	public class ChainIndex : ConcurrentChain
	{
		private BlockStore store;
		private IndexedBlockStore indexStore;

		public ChainedBlock LastIndexedBlock { get; private set; }

		public void Load(Context context)
		{
			// todo: create a repository that persists index data to file
			this.store = new BlockStore(context.Config.FolderLocation, context.Network);
			this.indexStore = new IndexedBlockStore(new InMemoryNoSqlRepository(), store);
			this.indexStore.ReIndex();
			this.LastIndexedBlock = this.FindLastIndexedBlock();
		}

		public void AddBlock(Block block)
		{
			block.SetPosParams();
			var header = this.GetBlock(block.GetHash());
			header.Header.PosParameters = block.Header.PosParameters;
			this.indexStore.Put(block);
			this.LastIndexedBlock = header;
		}

		public Block GetFullBlock(uint256 blockId)
		{
			return this.indexStore.Get(blockId);
		}

		private ChainedBlock FindLastIndexedBlock()
		{
			var current = this.Tip;

			while (current != this.Genesis)
			{
				if (indexStore.Get(current.HashBlock) != null)
					return current;
				current = current.Previous;
			}

			return this.Genesis;
		}
	}

	public class ChainSyncService : ITerminate
	{
		private readonly Context context;
		private readonly NodeConnectionService nodeConnectionService;

		public ChainIndex ChainIndex { get; }

		public ChainSyncService(Context context, NodeConnectionService nodeConnectionService)
		{
			this.context = context;
			this.ChainIndex = this.context.ChainIndex;
			this.nodeConnectionService = nodeConnectionService;
		}

		public ChainSyncService LoadHeaders()
		{
			// load headers form file (or genesis)
			if (File.Exists(this.context.Config.File("headers.dat")))
			{
				this.ChainIndex.Load(File.ReadAllBytes(this.context.Config.File("headers.dat")));
			}
			else
			{
				this.ChainIndex.SetTip(new ChainedBlock(this.context.Network.GetGenesis().Header, 0));
			}
			
			// load the index chain this will 
			// add each block index to memory for fast lookup
			this.ChainIndex.Load(this.context);


			// sync the headers and save to disk
			this.SyncChain(true);

			// enable sync on the behaviours 
			this.nodeConnectionService.EnableHeaderSyncing();

			return this;
		}

		public void SyncChain(bool saveToDisk = false)
		{
			// download all block headers up to current tip
			// this will loop until complete using a new node
			// if the current node got disconnected 
			var node = this.nodeConnectionService.GetNode(true);
			node.SynchronizeChain(ChainIndex, null, context.CancellationToken);

			if(saveToDisk)
				this.SaveChainToDisk();
		}

		private LockObject saveLock = new LockObject();
		private long savedHeight = 0;

		// this method is thread safe
		// it should be called periodically by a behaviour  
		// that is in charge of keeping the chin in sync
		public void SaveChainToDisk()
		{
			saveLock.Lock(() => this.ChainIndex.Tip.Height > savedHeight, () =>
				{
					using (var file = File.OpenWrite(this.context.Config.File("headers.dat")))
					{
						this.ChainIndex.WriteTo(file);
					}

					this.savedHeight = this.ChainIndex.Tip.Height;
				});
		}

		public void OnStop()
		{
			// stop the syncing behaviour

			// save the current header chain to disk
			this.SaveChainToDisk();
		}
	}
}