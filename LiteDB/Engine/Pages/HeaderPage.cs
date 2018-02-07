﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    internal class HeaderPage : BasePage
    {
        /// <summary>
        /// Represent maximum bytes that all collections names can be used in collection list page (must fit inside a single header page)
        /// </summary>
        public const ushort MAX_COLLECTIONS_NAME_SIZE = PAGE_SIZE - 1000;

        /// <summary>
        /// Page type = Header
        /// </summary>
        public override PageType PageType { get { return PageType.Header; } }

        /// <summary>
        /// Header info the validate that datafile is a LiteDB file (27 bytes)
        /// </summary>
        private const string HEADER_INFO = "** This is a LiteDB file **";

        /// <summary>
        /// Datafile specification version
        /// </summary>
        private const byte FILE_VERSION = 8;

        /// <summary>
        /// Hash Password in PBKDF2 [20 bytes]
        /// </summary>
        public byte[] Password { get; set; }

        /// <summary>
        /// When using encryption, store salt for password [16 bytes]
        /// </summary>
        public byte[] Salt { get; set; }

        /// <summary>
        /// Get/Set the pageID that start sequence with a complete empty pages (can be used as a new page)
        /// </summary>
        public uint FreeEmptyPageID;

        /// <summary>
        /// Last created page - Used when there is no free page inside file
        /// </summary>
        public uint LastPageID;

        /// <summary>
        /// DateTime when database was created
        /// </summary>
        public DateTime CreationTime { get; set; }

        /// <summary>
        /// DateTime when database was changed (commited)
        /// </summary>
        public DateTime LastCommit { get; set; }

        /// <summary>
        /// DateTime when database run checkpoint
        /// </summary>
        public DateTime LastCheckpoint { get; set; }

        /// <summary>
        /// DateTime when database run analyze
        /// </summary>
        public DateTime LastAnalyze { get; set; }

        /// <summary>
        /// DateTime when database run vaccum
        /// </summary>
        public DateTime LastVaccum { get; set; }

        /// <summary>
        /// DateTime when database run shrink
        /// </summary>
        public DateTime LastShrink { get; set; }

        /// <summary>
        /// Transaction commit counter - this counter reset after last vaccum/shrink
        /// </summary>
        public uint CommitCount { get; set; }

        /// <summary>
        /// Checkpoint counter - this counter reset after last vaccum/shrink
        /// </summary>
        public uint CheckpointCounter { get; set; }

        /// <summary>
        /// Contains all collection in database using PageID to direct access
        /// </summary>
        public ConcurrentDictionary<string, uint> Collections { get; set; }

        private HeaderPage()
        {
        }

        public HeaderPage(uint pageID)
            : base(pageID)
        {
            this.ItemCount = 0; // used to store collection names
            this.FreeBytes = 0; // no free bytes on header
            this.Password = new byte[20];
            this.Salt = new byte[16];
            this.FreeEmptyPageID = uint.MaxValue;
            this.LastPageID = 2; // LockPage
            this.CreationTime = DateTime.Now;
            this.LastCommit = DateTime.MinValue;
            this.LastCheckpoint = DateTime.MinValue;
            this.LastAnalyze = DateTime.MinValue;
            this.LastVaccum = DateTime.MinValue;
            this.LastShrink = DateTime.MinValue;
            this.CommitCount = 0;
            this.CheckpointCounter = 0;
            this.Collections = new ConcurrentDictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Update header page with confirm data
        /// </summary>
        public void Update(Guid transactionID, uint freeEmptyPageID, TransactionPages transPages)
        {
            this.TransactionID = transactionID;
            this.FreeEmptyPageID = freeEmptyPageID;
            this.CommitCount++;
            this.LastCommit = DateTime.Now;

            // remove/add collections based on transPages
            if (transPages != null)
            {
                foreach (var p in transPages.DeletedCollections)
                {
                    if (this.Collections.TryRemove(p.Key, out var x) == false)
                    {
                        throw LiteException.CollectionNotFound(p.Key);
                    }
                }

                foreach (var p in transPages.NewCollections)
                {
                    if (this.Collections.ContainsKey(p.Key))
                    {
                        throw LiteException.CollectionAlreadyExist(p.Key);
                    }
                    
                    this.Collections.TryAdd(p.Key, p.Value.PageID);
                }

                this.ItemCount = this.ItemCount - transPages.DeletedPages + transPages.NewCollections.Count;
            }
        }

        /// <summary>
        /// Check if all new collection names fit on header page with all existing collection
        /// </summary>
        public void CheckCollectionsSize(IEnumerable<string> names)
        {
            var sum =
                this.Collections.Sum(x => x.Key.Length + 8) +
                names.Sum(x => x.Length + 8);

            if (sum >= MAX_COLLECTIONS_NAME_SIZE)
            {
                throw LiteException.CollectionLimitExceeded(HeaderPage.MAX_COLLECTIONS_NAME_SIZE);
            }
        }

        #region Read/Write pages

        protected override void ReadContent(ByteReader reader)
        {
            var info = reader.ReadString(HEADER_INFO.Length);
            var ver = reader.ReadByte();

            if (info != HEADER_INFO) throw LiteException.InvalidDatabase();
            if (ver != FILE_VERSION) throw LiteException.InvalidDatabaseVersion(ver);

            this.Salt = reader.ReadBytes(this.Salt.Length);
            this.FreeEmptyPageID = reader.ReadUInt32();
            this.LastPageID = reader.ReadUInt32();
            this.CreationTime = reader.ReadDateTime();
            this.LastCommit = reader.ReadDateTime();
            this.LastCheckpoint = reader.ReadDateTime();
            this.LastAnalyze = reader.ReadDateTime();
            this.LastVaccum = reader.ReadDateTime();
            this.LastShrink = reader.ReadDateTime();
            this.CommitCount = reader.ReadUInt32();
            this.CheckpointCounter = reader.ReadUInt32();

            for (var i = 0; i < this.ItemCount; i++)
            {
                this.Collections.TryAdd(reader.ReadString(), reader.ReadUInt32());
            }
        }

        protected override void WriteContent(ByteWriter writer)
        {
            writer.Write(HEADER_INFO, HEADER_INFO.Length);
            writer.Write(FILE_VERSION);
            writer.Write(this.Salt);
            writer.Write(this.FreeEmptyPageID);
            writer.Write(this.LastPageID);
            writer.Write(this.CreationTime);
            writer.Write(this.LastCommit);
            writer.Write(this.LastCheckpoint);
            writer.Write(this.LastAnalyze);
            writer.Write(this.LastVaccum);
            writer.Write(this.LastShrink);
            writer.Write(this.CommitCount);
            writer.Write(this.CheckpointCounter);

            foreach (var col in this.Collections)
            {
                writer.Write(col.Key);
                writer.Write(col.Value);
            }
        }

        public override BasePage Clone()
        {
            return new HeaderPage
            {
                // base page
                PageID = this.PageID,
                PrevPageID = this.PrevPageID,
                NextPageID = this.NextPageID,
                ItemCount = this.ItemCount,
                FreeBytes = this.FreeBytes,
                TransactionID = this.TransactionID,
                // header page
                Password = this.Password,
                Salt = this.Salt,
                FreeEmptyPageID = this.FreeEmptyPageID,
                LastPageID = this.LastPageID,
                CreationTime = this.CreationTime,
                LastCommit = this.LastCommit,
                LastCheckpoint = this.LastCheckpoint,
                LastAnalyze = this.LastAnalyze,
                LastVaccum = this.LastVaccum,
                LastShrink = this.LastShrink,
                CommitCount = this.CommitCount,
                CheckpointCounter = this.CheckpointCounter,
                Collections = new ConcurrentDictionary<string, uint>(this.Collections)
            };
        }

        #endregion

    }
}