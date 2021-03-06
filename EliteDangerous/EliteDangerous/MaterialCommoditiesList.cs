﻿/*
 * Copyright © 2015 - 2016 EDDiscovery development team
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
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace EliteDangerousCore
{
    [System.Diagnostics.DebuggerDisplay("Mat {name} count {count} left {scratchpad}")]
    public class MaterialCommodities               // in memory version of it
    {
        public int count { get; set; }
        public double price { get; set; }
        public MaterialCommodityData Details { get; set; }

        public MaterialCommodities(MaterialCommodityData c)
        {
            count = scratchpad = 0;
            price = 0;
            this.Details = c;
        }

        public MaterialCommodities(MaterialCommodities c)
        {
            count = c.count;        // clone these
            price = c.price;
            this.Details = c.Details;       // can copy this, its fixed
        }

        public string category { get { return Details.category; } }
        public string name { get { return Details.name; } }
        public string fdname { get { return Details.fdname; } }
        public string type { get { return Details.type; } }
        public string shortname { get { return Details.shortname; } }
        public Color colour { get { return Details.colour; } }

        #region Static properties and methods linking to MaterialCommodity
        public static string CommodityCategory { get { return MaterialCommodityData.CommodityCategory; } }
        public static string MaterialRawCategory { get { return MaterialCommodityData.MaterialRawCategory; } }
        public static string MaterialEncodedCategory { get { return MaterialCommodityData.MaterialEncodedCategory; } }
        public static string MaterialManufacturedCategory { get { return MaterialCommodityData.MaterialManufacturedCategory; } }
        #endregion

        public int scratchpad { get; set; }        // for synthesis dialog..
    }


    public class MaterialCommoditiesList
    {
        private List<MaterialCommodities> list;

        // static BaseUtils.LogToFile log = new BaseUtils.LogToFile("c:\\code"); // debug

        public MaterialCommoditiesList()
        {
            list = new List<MaterialCommodities>();
        }

        public bool ContainsRares() // function on purpose
        {
            return list.FindIndex(x => x.type.Equals(MaterialCommodityData.CommodityTypeRareGoods) && x.count > 0) != -1;
        }

        public MaterialCommoditiesList Clone(bool clearzeromaterials, bool clearzerocommodities)       // returns a new copy of this class.. all items a copy
        {
            MaterialCommoditiesList mcl = new MaterialCommoditiesList();

            list.ForEach(item =>
            {
                bool commodity = item.category.Equals(MaterialCommodities.CommodityCategory);
                // if items, or commodity and not clear zero, or material and not clear zero, add
                if (item.count > 0 || (commodity && !clearzerocommodities) || (!commodity && !clearzeromaterials))
                    mcl.list.Add(item);
            });

            return mcl;
        }

        public List<MaterialCommodities> List { get { return list; } }

        public MaterialCommodities Find(string name) { return list.Find(x => x.name.Equals(name, StringComparison.InvariantCultureIgnoreCase)); }
        public MaterialCommodities FindFD(string fdname) { return list.Find(x => x.fdname.Equals(fdname, StringComparison.InvariantCultureIgnoreCase)); }

        public List<MaterialCommodities> Sort(bool commodity)
        {
            List<MaterialCommodities> ret = new List<MaterialCommodities>();

            if (commodity)
                ret = list.Where(x => x.category.Equals(MaterialCommodities.CommodityCategory)).OrderBy(x => x.type)
                           .ThenBy(x => x.name).ToList();
            else
                ret = list.Where(x => !x.category.Equals(MaterialCommodities.CommodityCategory)).OrderBy(x => x.name).ToList();

            return ret;
        }

        public int Count(string [] cats)    // for all types of cat, if item matches or does not, count
        {
            int total = 0;
            foreach (MaterialCommodities c in list)
            {
                if ( Array.IndexOf<string>(cats, c.category) != -1 )
                    total += c.count;
            }

            return total;
        }

        public int DataCount { get { return Count(new string[] { MaterialCommodities.MaterialEncodedCategory }); } }
        public int MaterialsCount { get { return Count(new string[] { MaterialCommodities.MaterialRawCategory, MaterialCommodities.MaterialManufacturedCategory }); } }
        public int CargoCount { get { return Count(new string[] { MaterialCommodities.CommodityCategory }); } }

        public int DataHash() { return list.GetHashCode(); }

        void Dump()
        {
            System.Diagnostics.Debug.Write(list.GetHashCode() + " ");
            foreach ( MaterialCommodities m in list )
            {
                System.Diagnostics.Debug.Write( "{" + m.GetHashCode() + " " + m.category + " " + m.fdname + " " + m.count + "}");
            }
            System.Diagnostics.Debug.WriteLine("");
        }

        // ifnorecatonsearch is used if you don't know if its a material or commodity.. for future use.

        private MaterialCommodities GetNewCopyOf(string cat, string fdname, SQLiteConnectionUser conn, bool ignorecatonsearch = false)
        {
            int index = list.FindIndex(x => x.fdname.Equals(fdname, StringComparison.InvariantCultureIgnoreCase) && (ignorecatonsearch || x.category.Equals(cat, StringComparison.InvariantCultureIgnoreCase)));

            if (index >= 0)
            {
                list[index] = new MaterialCommodities(list[index]);    // fresh copy..
                return list[index];
            }
            else
            {
                MaterialCommodityData mcdb = MaterialCommodityData.EnsurePresent(cat,fdname);    // get a MCDB of this
                MaterialCommodities mc = new MaterialCommodities(mcdb);        // make a new entry
                list.Add(mc);

                //log.WriteLine("MC Made:" + cat + " " + fdname + " >> " + mc.fdname + mc.name );

                return mc;
            }
        }

        // ignore cat is only used if you don't know what it is 
        public void Change(string cat, string fdname, int num, long price, SQLiteConnectionUser conn, bool ignorecatonsearch = false)
        {
            MaterialCommodities mc = GetNewCopyOf(cat, fdname, conn, ignorecatonsearch);
       
            double costprev = mc.count * mc.price;
            double costnew = num * price;
            mc.count = Math.Max(mc.count + num, 0);

            if (mc.count > 0 && num > 0)      // if bought (defensive with mc.count)
                mc.price = (costprev + costnew) / mc.count;       // price is now a combination of the current cost and the new cost. in case we buy in tranches

            //log.WriteLine("MC Change:" + cat + " " + fdname + " " + num + " " + mc.count);
        }

        public void Craft(string fdname, int num)
        {
            int index = list.FindIndex(x => x.fdname.Equals(fdname, StringComparison.InvariantCultureIgnoreCase));

            if (index >= 0)
            {
                MaterialCommodities mc = new MaterialCommodities(list[index]);      // new clone of
                list[index] = mc;       // replace ours with new one
                mc.count = Math.Max(mc.count - num, 0);

                //log.WriteLine("MC Craft:" + fdname + " " + num + " " + mc.count);
            }
        }

        public void Died()
        {
            list.RemoveAll(x => x.category.Equals(MaterialCommodities.CommodityCategory));      // empty the list of all commodities
        }

        public void Set(string cat, string fdname, int num, double price, SQLiteConnectionUser conn)
        {
            MaterialCommodities mc = GetNewCopyOf(cat, fdname, conn);

            mc.count = num;
            if (price > 0)
                mc.price = price;

            //log.WriteLine("MC Set:" + cat + " " + fdname + " " + num + " " + mc.count);
        }

        public void Clear(bool commodity)
        {
            //log.Write("MC Clear");
            for (int i = 0; i < list.Count; i++)
            {
                MaterialCommodities mc = list[i];
                if (commodity == (mc.category == MaterialCommodities.CommodityCategory))
                {
                    list[i] = new MaterialCommodities(list[i]);     // new clone of it we can change..
                    list[i].count = 0;  // and clear it
                    //log.Write(mc.fdname + ",");
                }
            }
            //log.Write("", true);
        }

        static public MaterialCommoditiesList Process(JournalEntry je, MaterialCommoditiesList oldml, SQLiteConnectionUser conn,
                                                        bool clearzeromaterials, bool clearzerocommodities)
        {
            MaterialCommoditiesList newmc = (oldml == null) ? new MaterialCommoditiesList() : oldml;

            if (je is IMaterialCommodityJournalEntry)
            {
                IMaterialCommodityJournalEntry e = je as IMaterialCommodityJournalEntry;
                newmc = newmc.Clone(clearzeromaterials, clearzerocommodities);          // so we need a new one, makes a new list, but copies the items..
                e.MaterialList(newmc, conn);
                // newmc.Dump();    // debug
            }

            return newmc;
        }
    }
}
