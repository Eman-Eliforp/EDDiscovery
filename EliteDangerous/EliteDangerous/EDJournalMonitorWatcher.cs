﻿/*
 * Copyright © 2016 EDDiscovery development team
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under
 * the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
 * ANY KIND, either express or implied. See the License for the specific language
 * governing permissions and limitations under the License.
 *
 * EDDiscovery is not affiliated with Frontier Developments plc.
 */

using EliteDangerousCore.DB;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Data.Common;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace EliteDangerousCore
{
    public class JournalReaderEntry
    {
        public JournalEntry JournalEntry;
        public JObject Json;
    }

    // watches a journal for changes, reads it, 

    public class JournalMonitorWatcher
    {
        public string m_watcherfolder;

        Dictionary<string, EDJournalReader> netlogreaders = new Dictionary<string, EDJournalReader>();
        EDJournalReader lastnfi = null;          // last one read..
        FileSystemWatcher m_Watcher;
        private int ticksNoActivity = 0;
        ConcurrentQueue<string> m_netLogFileQueue;

        public JournalMonitorWatcher(string folder)
        {
            m_watcherfolder = folder;
        }

        public void ParseJournalFiles(Func<bool> cancelRequested, Action<int, string> updateProgress, bool forceReload = false)
        {
            System.Diagnostics.Trace.WriteLine("Scanned " + m_watcherfolder);

            Dictionary<string, TravelLogUnit> m_travelogUnits = TravelLogUnit.GetAll().Where(t => (t.type & 0xFF) == 3).GroupBy(t => t.Name).Select(g => g.First()).ToDictionary(t => t.Name);

            // order by file write time so we end up on the last one written
            FileInfo[] allFiles = Directory.EnumerateFiles(m_watcherfolder, "Journal*.log", SearchOption.AllDirectories).Select(f => new FileInfo(f)).OrderBy(p => p.LastWriteTime).ToArray();

            List<EDJournalReader> readersToUpdate = new List<EDJournalReader>();

            for (int i = 0; i < allFiles.Length; i++)
            {
                FileInfo fi = allFiles[i];

                var reader = OpenFileReader(fi, m_travelogUnits);

                if (!m_travelogUnits.ContainsKey(reader.TravelLogUnit.Name))
                {
                    m_travelogUnits[reader.TravelLogUnit.Name] = reader.TravelLogUnit;
                    reader.TravelLogUnit.type = 3;
                    reader.TravelLogUnit.Add();
                }

                if (!netlogreaders.ContainsKey(reader.TravelLogUnit.Name))
                {
                    netlogreaders[reader.TravelLogUnit.Name] = reader;
                }

                if (forceReload)
                {
                    // Force a reload of the travel log
                    reader.TravelLogUnit.Size = 0;
                }

                if (reader.filePos != fi.Length || i == allFiles.Length - 1)  // File not already in DB, or is the last one
                {
                    readersToUpdate.Add(reader);
                }
            }

            for (int i = 0; i < readersToUpdate.Count; i++)
            {
                using (SQLiteConnectionUser cn = new SQLiteConnectionUser(utc: true))
                {
                    EDJournalReader reader = readersToUpdate[i];
                    updateProgress(i * 100 / readersToUpdate.Count, reader.TravelLogUnit.Name);

                    List<JournalReaderEntry> entries = reader.ReadJournalLog(true).ToList();      // this may create new commanders, and may write to the TLU db
                    ILookup<DateTime, JournalEntry> existing = JournalEntry.GetAllByTLU(reader.TravelLogUnit.id).ToLookup(e => e.EventTimeUTC);

                    using (DbTransaction tn = cn.BeginTransaction())
                    {
                        foreach (JournalReaderEntry jre in entries)
                        {
                            if (!existing[jre.JournalEntry.EventTimeUTC].Any(e => JournalEntry.AreSameEntry(jre.JournalEntry, e, ent1jo: jre.Json)))
                            {
                                System.Diagnostics.Trace.WriteLine(string.Format("Write Journal to db {0} {1}", jre.JournalEntry.EventTimeUTC, jre.JournalEntry.EventTypeStr));
                                jre.JournalEntry.Add(jre.Json, cn, tn);
                            }
                        }

                        tn.Commit();
                    }

                    reader.TravelLogUnit.Update(cn);

                    updateProgress((i + 1) * 100 / readersToUpdate.Count, reader.TravelLogUnit.Name);

                    lastnfi = reader;
                }
            }

            updateProgress(-1, "");
        }

        private EDJournalReader OpenFileReader(FileInfo fi, Dictionary<string, TravelLogUnit> tlu_lookup = null)
        {
            EDJournalReader reader;
            TravelLogUnit tlu;

            //System.Diagnostics.Trace.WriteLine(string.Format("File Read {0}", fi.FullName));

            if (netlogreaders.ContainsKey(fi.Name))
            {
                reader = netlogreaders[fi.Name];
            }
            else if (tlu_lookup != null && tlu_lookup.ContainsKey(fi.Name))
            {
                tlu = tlu_lookup[fi.Name];
                tlu.Path = fi.DirectoryName;
                reader = new EDJournalReader(tlu);
                netlogreaders[fi.Name] = reader;
            }
            else if (TravelLogUnit.TryGet(fi.Name, out tlu))
            {
                tlu.Path = fi.DirectoryName;
                reader = new EDJournalReader(tlu);
                netlogreaders[fi.Name] = reader;
            }
            else
            {
                reader = new EDJournalReader(fi.FullName);

#if false
                // Bring over the commander from the previous log if possible
                Match match = journalNamePrefixRe.Match(fi.Name);
                if (match.Success)
                {
                    string prefix = match.Groups["prefix"].Value;
                    string partstr = match.Groups["part"].Value;
                    int part;
                    if (Int32.TryParse(partstr, NumberStyles.Integer, CultureInfo.InvariantCulture, out part) && part > 1)
                    {
                        //EDCommander lastcmdr = EDDConfig.Instance.CurrentCommander;
                        var lastreader = netlogreaders.Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
                                                      .Select(k => k.Value)
                                                      .FirstOrDefault();
                        //if (lastreader != null)
                        //{
                            //lastcmdr = lastreader.Commander;
                        //}

                        //reader.Commander = lastcmdr;
                    }
                }
#endif
                netlogreaders[fi.Name] = reader;
            }

            return reader;
        }

        public void StartMonitor()
        {
            if (m_Watcher == null)
            {
                try
                {
                    m_netLogFileQueue = new ConcurrentQueue<string>();
                    m_Watcher = new System.IO.FileSystemWatcher();
                    m_Watcher.Path = m_watcherfolder + Path.DirectorySeparatorChar;
                    m_Watcher.Filter = "Journal.*.log";
                    m_Watcher.IncludeSubdirectories = false;
                    m_Watcher.NotifyFilter = NotifyFilters.FileName;
                    m_Watcher.Changed += new FileSystemEventHandler(OnNewFile);
                    m_Watcher.Created += new FileSystemEventHandler(OnNewFile);
                    m_Watcher.EnableRaisingEvents = true;

                    System.Diagnostics.Trace.WriteLine("Start Monitor on " + m_watcherfolder);
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show("Start Monitor exception : " + ex.Message, "EDDiscovery Error");
                    System.Diagnostics.Trace.WriteLine("Start Monitor exception : " + ex.Message);
                    System.Diagnostics.Trace.WriteLine(ex.StackTrace);
                }
            }
        }

        public void StopMonitor()
        {
            if (m_Watcher != null)
            {
                m_Watcher.EnableRaisingEvents = false;
                m_Watcher.Dispose();
                m_Watcher = null;

                System.Diagnostics.Trace.WriteLine("Stop Monitor on " + m_watcherfolder);
            }
        }

        // OS calls this when new file is available, add to list

        private void OnNewFile(object sender, FileSystemEventArgs e)        // only picks up new files
        {                                                                   // and it can kick in before any data has had time to be written to it...
            string filename = e.FullPath;
            m_netLogFileQueue.Enqueue(filename);
        }

        private string GetExtraInfoFileName(JournalTypeEnum jtype)
        {
            switch (jtype)
            {
                case JournalTypeEnum.Market: return "Market.json";
                case JournalTypeEnum.Outfitting: return "Outfitting.json";
                case JournalTypeEnum.Shipyard: return "Shipyard.json";
                case JournalTypeEnum.ModuleInfo: return "ModulesInfo.json";
                default: return null;
            }
        }

        private bool ReadExtraInfoFromFile(JournalEntry je, EDJournalReader rdr, out JournalReaderEntry jre)
        {
            jre = null;

            string extrafile = GetExtraInfoFileName(je.EventTypeID);

            if (extrafile == null)
            {
                return false;
            }

            extrafile = Path.Combine(Path.GetDirectoryName(rdr.FileName), extrafile);

            if (File.Exists(extrafile))
            {
                JObject jo = null;

                for (int retries = 0; retries < 5; retries++)
                {
                    try
                    {
                        string json = File.ReadAllText(extrafile);
                        if (json != null)
                        {
                            jo = JObject.Parse(json);
                            JournalEntry newje = JournalEntry.CreateJournalEntry(jo);

                            if (newje.EventTimeUTC == je.EventTimeUTC && newje.EventTypeStr == je.EventTypeStr)
                            {
                                jre = new JournalReaderEntry
                                {
                                    JournalEntry = newje,
                                    Json = jo
                                };

                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Unable to read extra info from {extrafile}: {ex.Message}");
                        Thread.Sleep(500);
                    }
                }
            }

            return false;
        }

        private void ScanReader(EDJournalReader nfi, List<JournalEntry> entries)
        {
            int netlogpos = 0;

            try
            {
                if (nfi.TravelLogUnit.id == 0)
                {
                    nfi.TravelLogUnit.type = 3;
                    nfi.TravelLogUnit.Add();
                }

                netlogpos = nfi.TravelLogUnit.Size;

                List<JournalReaderEntry> ents = nfi.ReadJournalLog().ToList();

                if (ents.Count > 0)
                {
                    using (SQLiteConnectionUser cn = new SQLiteConnectionUser(utc: true))
                    {
                        using (DbTransaction txn = cn.BeginTransaction())
                        {
                            ents = ents.Where(jre => JournalEntry.FindEntry(jre.JournalEntry, jre.Json).Count == 0).ToList();

                            foreach (JournalReaderEntry jre in ents)
                            {
                                JournalReaderEntry xjre = null;
                                if (ReadExtraInfoFromFile(jre.JournalEntry, nfi, out xjre))
                                {
                                    entries.Add(xjre.JournalEntry);
                                    jre.JournalEntry.Add(xjre.Json, cn, txn);
                                    ticksNoActivity = 0;
                                }
                                else
                                {
                                    entries.Add(jre.JournalEntry);
                                    jre.JournalEntry.Add(jre.Json, cn, txn);
                                    ticksNoActivity = 0;
                                }
                            }

                            nfi.TravelLogUnit.Update(cn);

                            txn.Commit();
                        }
                    }
                }
            }
            catch
            {
                // Revert and re-read the failed entries
                if (nfi != null && nfi.TravelLogUnit != null)
                {
                    nfi.TravelLogUnit.Size = netlogpos;
                }

                throw;
            }
        }

        // Called by EDJournalClass periodically to scan for journal entries

        public List<JournalEntry> ScanForNewEntries()
        {
            var entries = new List<JournalEntry>();
            EDJournalReader nfi = null;

            try
            {
                string filename = null;

                if (lastnfi != null)
                {
                    ScanReader(lastnfi, entries);
                }

                if (entries.Count != 0)
                {
                    return entries;
                }

                if (m_netLogFileQueue.TryDequeue(out filename))      // if a new one queued, we swap to using it
                {
                    nfi = OpenFileReader(new FileInfo(filename));
                    lastnfi = nfi;
                    System.Diagnostics.Trace.WriteLine(string.Format("Change in file, scan {0}", lastnfi.FileName));
                }
                else if (ticksNoActivity >= 30 && (lastnfi == null || (!File.Exists(lastnfi.FileName) || lastnfi.filePos >= new FileInfo(lastnfi.FileName).Length)))
                {
                    if (lastnfi == null)
                    {
                        Trace.Write($"No last file - scanning for journals");
                    }
                    else if (!File.Exists(lastnfi.FileName))
                    {
                        Trace.WriteLine($"File {lastnfi.FileName} not found - scanning for journals");
                    }
                    else
                    {
                        //                        Trace.WriteLine($"No activity on {lastnfi.FileName} for 60 seconds ({lastnfi.filePos} >= {new FileInfo(lastnfi.FileName).Length} - scanning for new journals");
                    }

                    HashSet<string> tlunames = new HashSet<string>(TravelLogUnit.GetAllNames());
                    string[] filenames = Directory.EnumerateFiles(m_watcherfolder, "Journal.*.log", SearchOption.AllDirectories)
                                                  .Select(s => new { name = Path.GetFileName(s), fullname = s })
                                                  .Where(s => !tlunames.Contains(s.name))
                                                  .OrderBy(s => s.name)
                                                  .Select(s => s.fullname)
                                                  .ToArray();
                    ticksNoActivity = 0;
                    foreach (var name in filenames)
                    {
                        nfi = OpenFileReader(new FileInfo(name));
                        lastnfi = nfi;
                        break;
                    }
                }
                else
                {
                    nfi = lastnfi;
                }

                ticksNoActivity++;

                if (nfi != null)
                {
                    ScanReader(nfi, entries);
                }

                return entries;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Net tick exception : " + ex.Message);
                System.Diagnostics.Trace.WriteLine(ex.StackTrace);
                return new List<JournalEntry>();
            }
        }

    }
}