//-----------------------------------------------------------------------
// <copyright file="SchemaCreator.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Globalization;
using System.Text;
using Microsoft.Isam.Esent.Interop;

namespace Raven.Storage.Esent
{
	[CLSCompliant(false)]
	public class SchemaCreator
	{
		public const string SchemaVersion = "3.6";
		private readonly Session session;

		public SchemaCreator(Session session)
		{
			this.session = session;
		}

		public void Create(string database)
		{
			JET_DBID dbid;
			Api.JetCreateDatabase(session, database, null, out dbid, CreateDatabaseGrbit.None);
			try
			{
				using (var tx = new Transaction(session))
				{
					CreateDetailsTable(dbid);
					CreateDocumentsTable(dbid);
					CreateDocumentsBeingModifiedByTransactionsTable(dbid);
				    CreateTransactionsTable(dbid);
					CreateTasksTable(dbid);
					CreateMapResultsTable(dbid);
					CreateIndexingStatsTable(dbid);
					CreateIndexingStatsReduceTable(dbid);
					CreateIndexingEtagsTable(dbid);
					CreateFilesTable(dbid);
					CreateQueueTable(dbid);
					CreateIdentityTable(dbid);

					tx.Commit(CommitTransactionGrbit.None);
				}
			}
			finally
			{
				Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
			}
		}

		private void CreateIdentityTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "identity_table", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);


			var defaultValue = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "val", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			CreateIndexes(tableid, new JET_INDEXCREATE
			{
				szKey = "+key\0\0",
				szIndexName = "by_key",
				grbit = CreateIndexGrbit.IndexPrimary
			});
		}

		private void CreateIndexingStatsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "indexes_stats", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			var defaultValue = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "successes", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnEscrowUpdate
			}, defaultValue, defaultValue.Length, out columnid);

			Api.JetAddColumn(session, tableid, "last_indexed_etag", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Binary,
				cbMax = 16,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);


			Api.JetAddColumn(session, tableid, "last_indexed_timestamp", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.DateTime,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "attempts", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			Api.JetAddColumn(session, tableid, "errors", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			CreateIndexes(tableid, new JET_INDEXCREATE
			{
				szIndexName = "by_key",
				szKey = "+key\0\0",
				grbit = CreateIndexGrbit.IndexPrimary
			});
		}

		// this table exists solely so that other threads can touch the index
		// etag, such as when we remove an item from the index, without causing
		// concurrency conflicts with the indexing thread
		private void CreateIndexingEtagsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "indexes_etag", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			var defaultValue = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "touches", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnEscrowUpdate
			}, defaultValue, defaultValue.Length, out columnid);

			CreateIndexes(tableid, new JET_INDEXCREATE
			{
				szIndexName = "by_key",
				szKey = "+key\0\0",
				grbit = CreateIndexGrbit.IndexPrimary
			});
		}

		private void CreateIndexingStatsReduceTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "indexes_stats_reduce", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			var defaultValue = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "reduce_successes", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnEscrowUpdate
			}, defaultValue, defaultValue.Length, out columnid);

			Api.JetAddColumn(session, tableid, "reduce_attempts", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			Api.JetAddColumn(session, tableid, "reduce_errors", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate | ColumndefGrbit.ColumnNotNULL
			}, defaultValue, defaultValue.Length, out columnid);

			Api.JetAddColumn(session, tableid, "last_reduced_etag", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Binary,
				cbMax = 16,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);


			Api.JetAddColumn(session, tableid, "last_reduced_timestamp", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.DateTime,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			CreateIndexes(tableid, new JET_INDEXCREATE
			{
				szIndexName = "by_key",
				szKey = "+key\0\0",
				grbit = CreateIndexGrbit.IndexPrimary
			});
		}

		private void CreateTransactionsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "transactions", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "tx_id", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "timeout", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.DateTime,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			CreateIndexes(tableid, new JET_INDEXCREATE
			{
				szIndexName = "by_tx_id",
				szKey = "+tx_id\0\0",
				grbit = CreateIndexGrbit.IndexPrimary
			});
		}

	    private void CreateDocumentsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "documents", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "etag", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "last_modified", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.DateTime,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "locked_by_transaction", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnTagged,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "metadata", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

	    	CreateIndexes(tableid,
	    	              new JET_INDEXCREATE
	    	              {
	    	              	szIndexName = "by_id",
	    	              	szKey = "+id\0\0",
	    	              	grbit = CreateIndexGrbit.IndexPrimary
	    	              },
	    	              new JET_INDEXCREATE
	    	              {
	    	              	szIndexName = "by_etag",
	    	              	szKey = "+etag\0\0",
	    	              	grbit = CreateIndexGrbit.IndexDisallowNull
	    	              },
	    	              new JET_INDEXCREATE
	    	              {
	    	              	szIndexName = "by_key",
	    	              	szKey = "+key\0\0",
	    	              	grbit = CreateIndexGrbit.IndexDisallowNull | CreateIndexGrbit.IndexUnique,
	    	              });
		}

		private const uint
			LCMAP_LOWERCASE = 0x00000100,
			LCMAP_UPPERCASE = 0x00000200,
			LCMAP_SORTKEY = 0x00000400,
			LCMAP_BYTEREV = 0x00000800,
			LCMAP_HIRAGANA = 0x00100000,
			LCMAP_KATAKANA = 0x00200000,
			LCMAP_HALFWIDTH = 0x00400000,
			LCMAP_FULLWIDTH = 0x00800000,
			LCMAP_LINGUISTIC_CASING = 0x01000000,
			LCMAP_SIMPLIFIED_CHINESE = 0x02000000,
			LCMAP_TRADITIONAL_CHINESE = 0x04000000;

		private const uint
			NORM_IGNORECASE = 1,
			NORM_IGNORENONSPACE = 2,
			NORM_IGNORESYMBOLS = 4,
			SORT_STRINGSORT = 0x01000,
			NORM_IGNOREKANATYPE = 0x10000,
			NORM_IGNOREWIDTH = 0x20000;

		private void CreateIndexes(JET_TABLEID tableid, params JET_INDEXCREATE[] indexes)
		{
			foreach (var index in indexes)
			{
				index.cbKey = index.szKey.Length;
				index.ulDensity = 90;
				index.cbKeyMost = SystemParameters.KeyMost;
				index.pidxUnicode = new JET_UNICODEINDEX
				{
				    lcid = CultureInfo.InvariantCulture.LCID,
				    dwMapFlags = LCMAP_SORTKEY | NORM_IGNORECASE | NORM_IGNOREKANATYPE | NORM_IGNOREWIDTH
				};
				try
				{
					Api.JetCreateIndex2(session, tableid, new[] { index }, 1);
				}
				catch (Exception e)
				{
					throw new InvalidOperationException("Could not create index: " + index.szIndexName, e);
				}
			}
		}

		private void CreateDocumentsBeingModifiedByTransactionsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "documents_modified_by_transaction", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);
			
			Api.JetAddColumn(session, tableid, "key", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "etag", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "last_modified", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.DateTime,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "locked_by_transaction", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnFixed,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "metadata", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "delete_document", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Bit,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			CreateIndexes(tableid,
				new JET_INDEXCREATE
				{
					szIndexName = "by_id",
					szKey = "+id\0\0",
 					grbit = CreateIndexGrbit.IndexPrimary
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_key",
					szKey = "+key\0\0",
					grbit = CreateIndexGrbit.IndexDisallowNull
				},
				new JET_INDEXCREATE
				{
					cbKeyMost = SystemParameters.KeyMost,
					grbit = CreateIndexGrbit.IndexDisallowNull,
					szIndexName = "by_tx",
					szKey = "+locked_by_transaction\0\0",
					ulDensity = 80,
				});
		}

		private void CreateMapResultsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "mapped_results", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "view", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "document_key", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "reduce_key", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.None
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "reduce_key_and_view_hashed", new JET_COLUMNDEF
			{
				cbMax = 32,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "etag", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnNotNULL|ColumndefGrbit.ColumnFixed
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "timestamp", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.DateTime,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed
			}, null, 0, out columnid);

			CreateIndexes(tableid,
				new JET_INDEXCREATE
				{
					szIndexName = "by_id",
					szKey = "+id\0\0",
					grbit = CreateIndexGrbit.IndexPrimary
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_view_and_doc_key",
					szKey	= "+view\0+document_key\0\0",
					grbit = CreateIndexGrbit.IndexDisallowNull
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_view",
					szKey = "+view\0\0",
					grbit = CreateIndexGrbit.IndexDisallowNull
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_view_and_etag",
					szKey = "+view\0-etag\0\0",
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_reduce_key_and_view_hashed",
					szKey = "+reduce_key_and_view_hashed\0\0",
				});
		}

		private void CreateTasksTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "tasks", 1, 80, out tableid);
			JET_COLUMNID columnid;

			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "task", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongText,
				grbit = ColumndefGrbit.None
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "supports_merging", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Bit,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "task_type", new JET_COLUMNDEF
			{
				cbMax = 255,
				coltyp = JET_coltyp.Text,
				cp = JET_CP.ASCII,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "for_index", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "added_at", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.DateTime,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			CreateIndexes(tableid,
			              new JET_INDEXCREATE
			              {
			              	szIndexName = "by_id",
			              	szKey = "+id\0\0",
			              	grbit = CreateIndexGrbit.IndexPrimary
			              },
			              new JET_INDEXCREATE
			              {
			              	szIndexName = "by_index",
			              	szKey = "+for_index\0\0",
			              	grbit = CreateIndexGrbit.IndexIgnoreNull
			              },
			              new JET_INDEXCREATE
			              {
			              	szIndexName = "mergables_by_task_type",
			              	szKey = "+supports_merging\0+for_index\0+task_type\0\0",
			              	grbit = CreateIndexGrbit.IndexIgnoreNull,
			              });
		}

		private void CreateFilesTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "files", 1, 80, out tableid);
			JET_COLUMNID columnid;


			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "name", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "etag", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnNotNULL,
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "metadata", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongText,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			CreateIndexes(tableid,
				new JET_INDEXCREATE
				{
					szIndexName = "by_id",
					szKey = "+id\0\0",
					grbit = CreateIndexGrbit.IndexPrimary
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_name",
					szKey = "+name\0\0",
					grbit = CreateIndexGrbit.IndexDisallowNull
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_etag",
					szKey = "+etag\0\0",
					grbit = CreateIndexGrbit.IndexDisallowNull
				});
		}

	    public void CreateQueueTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "queue", 1, 80, out tableid);
			JET_COLUMNID columnid;


			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnAutoincrement | ColumndefGrbit.ColumnNotNULL
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "name", new JET_COLUMNDEF
			{
				cbMax = 2048,
				coltyp = JET_coltyp.LongText,
				
				cp = JET_CP.Unicode,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			Api.JetAddColumn(session, tableid, "data", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.LongBinary,
				grbit = ColumndefGrbit.ColumnTagged
			}, null, 0, out columnid);

			var bytes = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "read_count", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate
			}, bytes, bytes.Length, out columnid);


			CreateIndexes(tableid,
				new JET_INDEXCREATE
				{
					szIndexName = "by_id",
					szKey = "+id\0\0",
					grbit = CreateIndexGrbit.IndexPrimary
				},
				new JET_INDEXCREATE
				{
					szIndexName = "by_name",
					szKey = "+name\0\0",
					grbit = CreateIndexGrbit.IndexDisallowNull
				});
		}
		
		private void CreateDetailsTable(JET_DBID dbid)
		{
			JET_TABLEID tableid;
			Api.JetCreateTable(session, dbid, "details", 1, 80, out tableid);
			JET_COLUMNID id;
			Api.JetAddColumn(session, tableid, "id", new JET_COLUMNDEF
			{
				cbMax = 16,
				coltyp = JET_coltyp.Binary,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed
			}, null, 0, out id);

			JET_COLUMNID schemaVersion;
			Api.JetAddColumn(session, tableid, "schema_version", new JET_COLUMNDEF
			{
				cbMax = 50,
				cp = JET_CP.Unicode,
				coltyp = JET_coltyp.Text,
				grbit = ColumndefGrbit.ColumnNotNULL
			}, null, 0, out schemaVersion);

			JET_COLUMNID documentCount;
			var bytes = BitConverter.GetBytes(0);
			Api.JetAddColumn(session, tableid, "document_count", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate
			}, bytes, bytes.Length, out documentCount);


			JET_COLUMNID attachmentCount;
			Api.JetAddColumn(session, tableid, "attachment_count", new JET_COLUMNDEF
			{
				coltyp = JET_coltyp.Long,
				grbit = ColumndefGrbit.ColumnNotNULL | ColumndefGrbit.ColumnFixed | ColumndefGrbit.ColumnEscrowUpdate
			}, bytes, bytes.Length, out attachmentCount);

			using (var update = new Update(session, tableid, JET_prep.Insert))
			{
				Api.SetColumn(session, tableid, id, Guid.NewGuid().ToByteArray());
				Api.SetColumn(session, tableid, schemaVersion, SchemaVersion, Encoding.Unicode);
				Api.SetColumn(session, tableid, documentCount, 0);
				Api.SetColumn(session, tableid, attachmentCount, 0);
				update.Save();
			}
		}
	}
}
