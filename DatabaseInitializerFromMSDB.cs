﻿using Serilog;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using SerilogTimings;
using System.ServiceProcess;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders.Physical;

namespace LogShippingService
{
    public class DatabaseInitializerFromMSDB: DatabaseInitializerBase
    {
        public override bool IsValidated => !string.IsNullOrEmpty(Config.SourceConnectionString);

        /// <summary>
        /// Check for new DBs in the source connection that don't exist in the destination.  
        /// </summary>
        protected override void PollForNewDBs()
        {
            List<DatabaseInfo> newDBs;
            using (Operation.Time("PollForNewDBs"))
            {
                newDBs = GetNewDatabases();
            }

            Log.Information("NewDBs:{Count}", newDBs.Count);
            Parallel.ForEach(newDBs.AsEnumerable(),
                new ParallelOptions() { MaxDegreeOfParallelism = Config.MaxThreads },
                newDb =>
                {
                    if (IsStopRequested) return;
                    try
                    {
                        if (LogShipping.InitializingDBs.TryAdd(newDb.Name.ToLower(), newDb.Name)) // To prevent log restores until initialization is complete
                        {
                            ProcessDB(newDb.Name);
                        }
                        else
                        {
                            Log.Error("{db} is already initializing",newDb.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error initializing new database from backup {db}", newDb.Name);
                    }
                    finally
                    {
                        LogShipping.InitializingDBs.TryRemove(newDb.Name.ToLower(),out _); // Log restores can start after restore operations have completed
                    }
                });
        }


        /// <summary>
        /// Get the last FULL/DIFF backup for the database from msdb history & restore
        /// </summary>
        /// <param name="db">Database name</param>
        private void ProcessDB(string db)
        {
            if (Config.SourceConnectionString == null) return;
            if (!IsValidForInitialization(db)) return;
            Log.Information("Initializing new database: {db}", db);
            var lastFull = new LastBackup(db, Config.SourceConnectionString, BackupHeader.BackupTypes.DatabaseFull );
            var lastDiff = new LastBackup(db, Config.SourceConnectionString, BackupHeader.BackupTypes.DatabaseDiff );

            if (lastFull.FileList.Count == 0)
            {
                Log.Error("No backups available to initialize {db}", db);
                return;
            }

            Log.Debug("Last full for {db}: {lastFull}",db,lastFull.BackupFinishDate);
            Log.Debug("Last diff for {db}: {lastDiff}", db, lastDiff.BackupFinishDate);

            var fullHeader = lastFull.GetHeader(Config.ConnectionString);

            lastFull.Restore();

            // Check if diff backup should be applied
            if (lastDiff.BackupFinishDate <= lastFull.BackupFinishDate) return;

            var diffHeader = lastDiff.GetHeader(Config.ConnectionString);
            if (diffHeader.BackupFinishDate > fullHeader.BackupFinishDate && fullHeader.FirstLSN == diffHeader.DifferentialBaseLSN)
            {
                lastDiff.Restore();
            }
        }

        
       /// <summary>
       /// Get a list of databases that exist in the source connection that don't exist in the destination.   Only include ONLINE databases with FULL or BULK LOGGED recovery model
       /// </summary>
       /// <returns></returns>
       private List<DatabaseInfo> GetNewDatabases()
        {
            if (Config.SourceConnectionString == null) return new List<DatabaseInfo>();
            if(DestinationDBs==null) return new List<DatabaseInfo>();

            var sourceDBs = DatabaseInfo.GetDatabaseInfo(Config.SourceConnectionString);

            sourceDBs = sourceDBs.Where(db => (db.RecoveryModel is 1 or 2 || Config.InitializeSimple) && db.State == 0).ToList();

            var newDBs = sourceDBs.Where(db =>
                !DestinationDBs.Any(destDb => destDb.Name.Equals(db.Name, StringComparison.OrdinalIgnoreCase))).ToList();

            return newDBs;
        }
    }
}
