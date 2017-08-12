﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SFarmer = StardewValley.Farmer;

namespace Replanter
{
    public class Replanter : Mod
    {
        /*********
        ** Properties
        *********/
        // The hot key which activates this mod.
        private Keys ActionKey;

        // A dictionary that allows seeds to be looked up by the crop's id.
        private Dictionary<int, int> CropToSeed;

        // A dictionary that allows the seed price to be looked up by the seed's id.
        private Dictionary<int, int> SeedToPrice;

        // A dictionary that allows chests to be looked up per item id, in case you've got somewhere else for that stuff to go.
        private Dictionary<int, Vector2> ChestLookup;

        // A dictionary that allows the crop price to be looked up by the crop's id.
        // private Dictionary<int, int> cropToPrice = null;

        // A list of crops that are never to be harvested.
        private HashSet<int> IgnoreLookup;

        // A list of crops that are always to be sold, regardless of "sellAfterHarvest" setting.  This list trumps never-sell in the case of duplicates.
        private HashSet<int> AlwaysSellLookup;

        // A list of crops that are never sold, regardless of sellAfterHarvest setting.
        private HashSet<int> NeverSellLookup;

        // The mod's configuration file.
        private ReplanterConfig Config;

        // Whether to disregard all cost mechanisms, or to enforce them.
        private bool Free;

        // A discount (integer converted to a percentage) received on seeds.
        private int SeedDiscount;

        // Whether to display dialogues and message feedback to the farmer.
        private bool MessagesEnabled = true;

        // The cost charged for the action of harvesting a crop, this is calculated per-crop.
        private float CostPerHarvestedCrop = 0.5f;

        // Whether or not to immediately sell the crops that are harvested-- they bypass both the inventory and chests, and the money is received immediately.
        private bool SellAfterHarvest;

        // Whether or not to water the crops after they are replanted.
        private bool WaterCrops;

        // A bar separated list of crop Ids that are to be ignored by the harvester.
        private string IgnoreList = "";

        // A bar separated list of crops that are to be always sold, regardless if sellAfterHarvest is false.
        private string AlwaysSellList = "";

        // A bar separated list of crops that are to never be sold, regardless if sellAfterHarvest is true.
        private string NeverSellList = "";

        private bool ClearDeadPlants = true;

        private bool SmartReplantingEnabled = true;

        // The name of the person or character who "checks" the crops, and will participate in dialogues following the action.
        private string Checker = "spouse";

        // Whether to bypass the inventory, and first attempt to deposit the harvest into the chest.  Inventory is then used as fallback.
        private bool BypassInventory;

        // The coordinates of a chest where crops are to be put if there is not room in your inventory.
        private Vector2 ChestCoords = new Vector2(70f, 14f);

        private String ChestDefs = "";

        private Dictionary<int, ChestDef> Chests;


        #region Messages and Dialogue

        // Content manager for loading dialogs, etc.
        private LocalizedContentManager Content;

        // An indexed list of all messages from the dialog.xna file
        private Dictionary<string, string> AllMessages;

        // An indexed list of key dialog elements, these need to be indexed in the order in the file ie. cannot be randomized.
        private Dictionary<int, string> Dialogue;

        // An indexed list of greetings.
        private Dictionary<int, string> Greetings;

        // An indexed list of all dialog entries relating to "unfinished money"
        //private Dictionary<int, string> unfinishedMessages = null;

        // An indexed list of all dialog entries related to "freebies"
        private Dictionary<int, string> FreebieMessages;

        // An indexed list of all dialog entries related to "inventory full"
        private Dictionary<int, string> InventoryMessages;

        // An indexed list of all dialog entries related to "smalltalk".  This list is merged with a list of dialogs that are specific to your "checker"
        private Dictionary<int, string> Smalltalk;

        // Random number generator, used primarily for selecting dialog messages.
        private Random Random = new Random();

        #endregion

        // A flag for when an item could not be deposited into either the inventory or the chest.
        private bool InventoryAndChestFull;

        private DialogueManager DialogueManager;


        /*********
        ** Public methods
        *********/
        public override void Entry(params object[] objects)
        {
            // load config
            this.Config = this.Helper.ReadConfig<ReplanterConfig>();
            this.ImportConfiguration();

            // load dialogue manager
            this.DialogueManager = new DialogueManager(this.Config, Game1.content.ServiceProvider, Game1.content.RootDirectory, this.Monitor);

            // hook events
            PlayerEvents.LoadedGame += this.PlayerEvents_LoadedGame;
            ControlEvents.KeyReleased += this.ControlEvents_KeyReleased;
        }


        /*********
        ** Private methods
        *********/
        private void PlayerEvents_LoadedGame(object sender, EventArgs e)
        {
            // Parses the always sell, never sell, and never harvest lists.
            this.GenerateLists();

            // Parses the lookup dictionaries for seed price and item info.
            this.GenerateDictionaries();

            this.ParseChestLocations();

            // Read in dialogue
            this.Content = new LocalizedContentManager(Game1.content.ServiceProvider, this.PathOnDisk);
            this.ReadInMessages();
        }

        private void ControlEvents_KeyReleased(object sender, EventArgsKeyPressed e)
        {
            if (Game1.currentLocation == null
                || (Game1.player == null
                || Game1.hasLoadedGame == false)
                || ((Game1.player).UsingTool
                || !(Game1.player).CanMove
                || (Game1.activeClickableMenu != null
                || Game1.CurrentEvent != null))
                || Game1.gameMode != 3)
                return;

            if (e.KeyPressed == this.ActionKey)
            {
                try
                {
                    this.PerformAction();
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Exception: {ex}", LogLevel.Error);
                }

            }
        }

        private void ImportConfiguration()
        {
            if (!Enum.TryParse(this.Config.KeyBind, true, out this.ActionKey))
            {
                this.ActionKey = Keys.J;
                this.Monitor.Log("Error parsing key binding. Defaulted to J");
            }

            this.MessagesEnabled = this.Config.EnableMessages;

            this.Free = this.Config.Free;

            if (this.Config.SeedDiscount > 100)
                this.SeedDiscount = 100;
            else if (this.Config.SeedDiscount < 0)
                this.SeedDiscount = 0;
            else
                this.SeedDiscount = this.Config.SeedDiscount;

            this.CostPerHarvestedCrop = this.Config.CostPerCropHarvested;
            this.SellAfterHarvest = this.Config.SellHarvestedCropsImmediately;
            this.Checker = this.Config.WhoChecks;
            this.WaterCrops = this.Config.WaterCrops;

            this.BypassInventory = this.Config.BypassInventory;
            this.ChestCoords = this.Config.ChestCoords;

            this.NeverSellList = this.Config.NeverSellList;
            this.AlwaysSellList = this.Config.AlwaysSellList;
            this.IgnoreList = this.Config.IgnoreList;

            this.ChestDefs = this.Config.ChestDefs;

            this.ClearDeadPlants = this.Config.ClearDeadPlants;
            this.SmartReplantingEnabled = this.Config.SmartReplantingEnabled;
        }

        private void PerformAction()
        {
            Farm farm = Game1.getFarm();
            SFarmer farmer = Game1.player;

            ReplanterStats stats = new ReplanterStats();

            foreach (GameLocation location in Game1.locations)
            {
                bool itemHarvested = true;

                if (!location.isFarm && !location.name.Contains("Greenhouse")) continue;

                foreach (KeyValuePair<Vector2, TerrainFeature> feature in location.terrainFeatures)
                {
                    if (feature.Value == null) continue;

                    if (feature.Value is HoeDirt)
                    {

                        HoeDirt dirt = (HoeDirt)feature.Value;

                        if (dirt.crop != null)
                        {
                            Crop crop = dirt.crop;

                            if (this.WaterCrops && dirt.state != 1)
                            {
                                dirt.state = 1;
                                stats.CropsWatered++;
                            }

                            if (this.ClearDeadPlants && crop.dead)
                            {
                                // TODO: store what kind of crop this was so we can replant.
                                dirt.destroyCrop(feature.Key);
                                stats.PlantsCleared++;

                                continue;
                            }

                            if (this.Ignore(crop.indexOfHarvest))
                            {
                                continue;
                            }

                            if (crop.currentPhase >= crop.phaseDays.Count - 1 && (!crop.fullyGrown || crop.dayOfCurrentPhase <= 0))
                            {
                                int seedCost = 0;
                                stats.TotalCrops++;

                                StardewValley.Object item = this.GetHarvestedCrop(dirt, crop, (int)feature.Key.X, (int)feature.Key.Y);

                                if (!this.Free)
                                {
                                    seedCost = (int)(this.CostOfSeed(crop.indexOfHarvest) * ((100f - this.SeedDiscount) / 100f));
                                }
                                if (this.SellAfterHarvest)
                                {
                                    if (this.SellCrops(farmer, item, stats))
                                    {
                                        if (crop.indexOfHarvest == 431)
                                            this.HandleSunflower(farmer, stats, item.quality);

                                        itemHarvested = true;
                                    }
                                    else
                                    {
                                        itemHarvested = false;
                                    }


                                }
                                else
                                {
                                    if (this.AddItemToInventory(item, farmer, farm, stats))
                                    {
                                        itemHarvested = true;

                                        if (crop.indexOfHarvest == 431)
                                        {
                                            this.HandleSunflower(farmer, stats, item.quality, (int)feature.Key.X, (int)feature.Key.Y);
                                        }
                                    }
                                    else
                                    {
                                        itemHarvested = false;
                                    }

                                }

                                // Replanting

                                if (itemHarvested)
                                {
                                    stats.CropsHarvested++;

                                    if (this.ReplantCrop(crop, location))
                                    {
                                        if (crop.regrowAfterHarvest == -1)
                                        {
                                            stats.RunningSeedCost += seedCost;
                                        }
                                    }
                                    else
                                    {
                                        if (crop.dead)
                                        {
                                            // Store what kind of crop this is so you can replant.
                                            dirt.destroyCrop(feature.Key);
                                            stats.PlantsCleared++;
                                        }
                                    }


                                    // Add experience
                                    float experience = (float)(16.0 * Math.Log(0.018 * Convert.ToInt32(Game1.objectInformation[crop.indexOfHarvest].Split('/')[1]) + 1.0, Math.E));
                                    Game1.player.gainExperience(0, (int)Math.Round(experience));
                                }

                            }
                        }
                    }
                    else if (feature.Value is FruitTree)
                    {
                        FruitTree tree = (FruitTree)feature.Value;

                        if (tree.fruitsOnTree > 0)
                        {
                            int countFromThisTree = 0;

                            for (int i = 0; i < tree.fruitsOnTree; i++)
                            {
                                stats.TotalCrops++;

                                StardewValley.Object fruit = this.GetHarvestedFruit(tree);

                                if (this.SellAfterHarvest)
                                {
                                    if (this.SellCrops(farmer, fruit, stats))
                                    {
                                        itemHarvested = true;
                                        countFromThisTree++;
                                    }
                                    else
                                        itemHarvested = false;
                                }
                                else
                                {
                                    if (this.AddItemToInventory(fruit, farmer, farm, stats))
                                    {
                                        itemHarvested = true;
                                        countFromThisTree++;
                                    }
                                    else
                                        itemHarvested = false;
                                }

                                if (itemHarvested)
                                {
                                    stats.CropsHarvested++;

                                    float experience = (float)(16.0 * Math.Log(0.018 * Convert.ToInt32(Game1.objectInformation[tree.indexOfFruit].Split('/')[1]) + 1.0, Math.E));
                                    Game1.player.gainExperience(0, (int)Math.Round(experience));
                                }
                            }

                            tree.fruitsOnTree -= countFromThisTree;
                        }


                    }
                }
            }

            if (stats.RunningSellPrice > 0)
            {
                farmer.Money = farmer.Money + stats.RunningSellPrice;
                this.Monitor.Log($"Sale price of your crops: {stats.RunningSellPrice}", LogLevel.Trace);
            }

            if (!this.Free)
            {
                farmer.Money = Math.Max(0, farmer.Money - stats.RunningSeedCost);
                this.Monitor.Log($"Total cost of new seeds: {stats.RunningSeedCost}", LogLevel.Trace);
            }

            if (!this.Free)
            {
                stats.FarmhandCost = (int)Math.Round(stats.CropsHarvested * this.CostPerHarvestedCrop);
                farmer.Money = Math.Max(0, farmer.Money - stats.FarmhandCost);
                this.Monitor.Log($"Costs paid to farm hand: {stats.FarmhandCost}", LogLevel.Trace);
            }

            if (this.MessagesEnabled)
            {
                this.ShowMessage(stats);
            }
        }

        private StardewValley.Object GetHarvestedFruit(FruitTree tree)
        {
            int quality = 0;
            if (tree.daysUntilMature <= -112)
                quality = 1;
            if (tree.daysUntilMature <= -224)
                quality = 2;
            if (tree.daysUntilMature <= -336)
                quality = 4;
            //if (tree.struckByLightningCountdown > 0)
            //        quality = 0; 

            StardewValley.Object obj = new StardewValley.Object(tree.indexOfFruit, 1, false, -1, quality);

            return obj;
        }

        private StardewValley.Object GetHarvestedCrop(HoeDirt dirt, Crop crop, int tileX, int tileY)
        {
            int stackSize = 1;
            int itemQuality = 0;
            int fertilizerBuff = 0;

            Random random = new Random(tileX * 7 + tileY * 11 + (int)Game1.stats.DaysPlayed + (int)Game1.uniqueIDForThisGame);

            switch (dirt.fertilizer)
            {
                case 368:
                    fertilizerBuff = 1;
                    break;
                case 369:
                    fertilizerBuff = 2;
                    break;
            }

            double qualityModifier1 = 0.2 * (Game1.player.FarmingLevel / 10) + 0.2 * fertilizerBuff * ((Game1.player.FarmingLevel + 2) / 12.0) + 0.01;
            double qualityModifier2 = Math.Min(0.75, qualityModifier1 * 2.0);

            if (random.NextDouble() < qualityModifier1)
                itemQuality = 2;
            else if (random.NextDouble() < qualityModifier2)
                itemQuality = 1;
            if (crop.minHarvest > 1 || crop.maxHarvest > 1)
                stackSize = random.Next(crop.minHarvest, Math.Min(crop.minHarvest + 1, crop.maxHarvest + 1 + Game1.player.FarmingLevel / crop.maxHarvestIncreasePerFarmingLevel));
            if (crop.chanceForExtraCrops > 0.0)
            {
                while (random.NextDouble() < Math.Min(0.9, crop.chanceForExtraCrops))
                    ++stackSize;
            }

            if (random.NextDouble() < Game1.player.LuckLevel / 1500.0 + Game1.dailyLuck / 1200.0 + 9.99999974737875E-05)
            {
                stackSize *= 2;
            }

            if (crop.indexOfHarvest == 421)
            {
                crop.indexOfHarvest = 431;
                stackSize = random.Next(1, 4);
            }

            StardewValley.Object item = new StardewValley.Object(crop.indexOfHarvest, stackSize, false, -1, itemQuality);

            return item;
        }

        private bool ReplantCrop(Crop c, GameLocation location)
        {
            bool replanted = true;
            String replantLog = "";

            if (this.SmartReplantingEnabled && !location.Name.Contains("Greenhouse"))
            {
                string season = Game1.currentSeason;
                string nextSeason = this.GetNextSeason(season);
                int day = Game1.dayOfMonth;
                int growingDaysTillNextSeason = 28 - day;
                int totalDaysNeeded = c.phaseDays.Sum();

                replantLog += "smartenabled/dtg:" + "?" + "/gdtns:" + growingDaysTillNextSeason + "/ioh:" + c.indexOfHarvest + "/tdn:" + totalDaysNeeded + "/";

                if (c.regrowAfterHarvest == -1)
                {
                    replantLog += "rah:" + c.regrowAfterHarvest + "/cpb:" + c.currentPhase + "/";
                    //c.currentPhase = 0;

                    if (!((totalDaysNeeded - 99999) <= growingDaysTillNextSeason) && !c.seasonsToGrowIn.Contains(nextSeason))
                    {
                        replantLog += "notreplanted/";
                        c.dead = true;
                        replanted = false;
                    }
                    else
                    {
                        replantLog += "replanted/";
                        c.currentPhase = 0;
                        replantLog += "cpa:" + c.currentPhase + "/";
                    }
                }
                else
                {
                    replantLog += "cp:" + c.currentPhase + "/rah:" + c.regrowAfterHarvest + "/docpb:" + c.dayOfCurrentPhase + "/";
                    c.dayOfCurrentPhase = c.regrowAfterHarvest;
                    c.fullyGrown = true;
                    replantLog += "docpa:" + c.dayOfCurrentPhase + "/";
                }
            }
            else
            {
                replantLog += "smartdisabled/";
                if (c.regrowAfterHarvest == -1)
                {
                    replantLog += "rah:" + c.regrowAfterHarvest + "/cpb:" + c.currentPhase + "/";
                    c.currentPhase = 0;
                    replantLog += "cpa:" + c.currentPhase + "/";
                }
                else
                {
                    replantLog += "cp:" + c.currentPhase + "/rah:" + c.regrowAfterHarvest + "/docpb:" + c.dayOfCurrentPhase + "/";
                    c.dayOfCurrentPhase = c.regrowAfterHarvest;
                    c.fullyGrown = true;
                    replantLog += "docpa:" + c.dayOfCurrentPhase + "/";
                }
            }

            replantLog += "replanted?:" + replanted;

            this.Monitor.Log(replantLog, LogLevel.Trace);

            return replanted;
        }

        private string GetNextSeason(string season)
        {
            switch (season)
            {
                case "spring":
                    return "summer";
                case "summer":
                    return "fall";
                case "fall":
                    return "winter";
                case "winter":
                    return "spring";
                default:
                    return "spring";
            }
        }

        private void HandleSunflower(SFarmer farmer, ReplanterStats stats, int quality, int tileX = 0, int tileY = 0)
        {
            if (this.SellAfterHarvest)
            {
                StardewValley.Object flower = new StardewValley.Object(421, 1, false, -1, quality);

                if (!this.SellCrops(farmer, flower, stats))
                {
                    // TODO: what to do if we can't sell the sunflower?
                }

            }
            else
            {
                StardewValley.Object flower = new StardewValley.Object(421, 1, false, -1, quality);

                if (!this.AddItemToInventory(flower, farmer, Game1.getFarm(), stats))
                {
                    Game1.createObjectDebris(421, tileX, tileY, -1, flower.quality);

                    this.Monitor.Log("Sunflower was harvested, but couldn't add flower to inventory, you'll have to go pick it up.", LogLevel.Trace);
                }

            }
        }

        private void ShowMessage(ReplanterStats stats)
        {
            string message = "";

            if (this.Checker.ToLower() == "spouse")
            {
                if (Game1.player.isMarried())
                {
                    message += this.DialogueManager.PerformReplacement(this.Dialogue[1], stats, this.Config);
                }
                else
                {
                    message += this.DialogueManager.PerformReplacement(this.Dialogue[2], stats, this.Config);
                }

                if (((stats.RunningSeedCost + stats.FarmhandCost) > 0) && !this.Free)
                {
                    message += this.DialogueManager.PerformReplacement(this.Dialogue[3], stats, this.Config);
                }

                HUDMessage msg = new HUDMessage(message);
                Game1.addHUDMessage(msg);
            }
            else
            {
                NPC character = Game1.getCharacterFromName(this.Checker);
                if (character != null)
                {
                    message += this.DialogueManager.PerformReplacement(this.GetRandomMessage(this.Greetings), stats, this.Config);

                    if (stats.CropsHarvested > 0)
                    {
                        message += this.DialogueManager.PerformReplacement(this.Dialogue[4], stats, this.Config);
                    }
                    else
                    {
                        message += this.DialogueManager.PerformReplacement(this.Dialogue[7], stats, this.Config);
                    }

                    if ((stats.CropsHarvested != stats.TotalCrops) && !this.SellAfterHarvest)
                    {
                        message += this.DialogueManager.PerformReplacement(this.Dialogue[8], stats, this.Config);
                        message += this.DialogueManager.PerformReplacement(this.GetRandomMessage(this.InventoryMessages), stats, this.Config);
                    }

                    if (!this.Free && stats.CropsHarvested > 0)
                    {
                        message += this.DialogueManager.PerformReplacement(this.Dialogue[5], stats, this.Config);

                        if (stats.RunningSeedCost > 0)
                            message += this.DialogueManager.PerformReplacement(this.Dialogue[9], stats, this.Config);
                        else
                            message += ".";
                    }

                    if (this.SellAfterHarvest && stats.CropsHarvested > 0)
                    {
                        if (character.name == "Pierre")
                        {
                            message += this.DialogueManager.PerformReplacement(this.Dialogue[10], stats, this.Config);
                        }
                        else
                        {
                            message += this.DialogueManager.PerformReplacement(this.Dialogue[11], stats, this.Config);
                        }
                    }

                    if (stats.CropsWatered > 0)
                    {
                        message += this.DialogueManager.PerformReplacement(this.Dialogue[12], stats, this.Config);
                    }

                    message += this.DialogueManager.PerformReplacement(this.GetRandomMessage(this.Smalltalk), stats, this.Config);
                    message += "#$e#";

                    character.CurrentDialogue.Push(new Dialogue(message, character));
                    Game1.drawDialogue(character);
                }
                else
                {
                    message += this.DialogueManager.PerformReplacement(this.Dialogue[13], stats, this.Config);
                    HUDMessage msg = new HUDMessage(message);
                    Game1.addHUDMessage(msg);
                }
            }

        }

        /**
         * Parses the neverSell, alwaysSell, neverHarvest crops.
         */
        private void GenerateLists()
        {
            this.IgnoreLookup = new HashSet<int>();
            if (this.IgnoreList.Length > 0)
            {
                string[] ignoredItems = this.IgnoreList.Split('|');
                foreach (string ignored in ignoredItems)
                {
                    this.IgnoreLookup.Add(Convert.ToInt32(ignored));
                }
            }

            this.AlwaysSellLookup = new HashSet<int>();
            if (this.AlwaysSellList.Length > 0)
            {
                string[] alwaysSellItems = this.AlwaysSellList.Split('|');
                foreach (string always in alwaysSellItems)
                {
                    this.AlwaysSellLookup.Add(Convert.ToInt32(always));
                }
            }

            this.NeverSellLookup = new HashSet<int>();
            if (this.NeverSellList.Length > 0)
            {
                string[] neverSellItems = this.NeverSellList.Split('|');
                foreach (string always in neverSellItems)
                {
                    this.NeverSellLookup.Add(Convert.ToInt32(always));
                }
            }
        }

        private void ParseChestLocations()
        {
            this.Chests = new Dictionary<int, ChestDef>();

            string[] chestDefinitions = this.ChestDefs.Split('|');

            foreach (string def in chestDefinitions)
            {
                string[] chestInfo = def.Split(',');
                if (chestInfo.Length == 3)
                {
                    // A Farm chest
                    ChestDef cd = new ChestDef(Convert.ToInt32(chestInfo[1]), Convert.ToInt32(chestInfo[2]));
                    this.Chests.Add(Convert.ToInt32(chestInfo[0]), cd);
                }
                else if (chestInfo.Length == 4)
                {
                    // A farm house chest
                    ChestDef cd = new ChestDef(Convert.ToInt32(chestInfo[1]), Convert.ToInt32(chestInfo[2]));
                    cd.Location = "house";
                    this.Chests.Add(Convert.ToInt32(chestInfo[0]), cd);
                }
            }
        }

        /**
         * Generates the lookup dictionaries needed to get crop and seed prices.
         */
        private void GenerateDictionaries()
        {
            Dictionary<int, string> dictionary = Game1.content.Load<Dictionary<int, string>>("Data\\Crops");
            Dictionary<int, string> objects = Game1.content.Load<Dictionary<int, string>>("Data\\ObjectInformation");

            this.CropToSeed = new Dictionary<int, int>();
            //cropToPrice = new Dictionary<int, int>();
            this.SeedToPrice = new Dictionary<int, int>();

            foreach (KeyValuePair<int, string> crop in dictionary)
            {
                string[] cropString = crop.Value.Split('/');

                int seedId = crop.Key;
                int harvestId = Convert.ToInt32(cropString[3]);

                if (seedId != 770)
                {
                    this.CropToSeed.Add(harvestId, seedId);
                }

                // Both of these are indexed by the harvest ID.
                string[] seedData = objects[seedId]?.Split('/');

                if (seedData != null)
                {
                    int seedCost = Convert.ToInt32(seedData[1]) * 2;

                    if (harvestId == 421)
                    {
                        this.SeedToPrice.Add(431, seedCost);
                    }
                    else
                    {
                        this.SeedToPrice.Add(harvestId, seedCost);
                    }

                    this.Monitor.Log($"Adding ID to seed price index: {harvestId} / {seedCost}", LogLevel.Trace);
                }
            }


        }

        /**
         * Gets the cost of the seed based off its harvest ID, (not the item id of the seed)
         */
        private int CostOfSeed(int id)
        {
            if (!this.SeedToPrice.ContainsKey(id))
            {
                this.Monitor.Log($"[Replanter] Couldn\'t find seed for harvest ID: {id}", LogLevel.Error);

                return 20;
            }
            else
                return this.SeedToPrice[id];
        }

        /**
         * Sells the crops, and adds them to the inventory if they are on the never-sell list.
         */
        private bool SellCrops(SFarmer farmer, StardewValley.Object obj, ReplanterStats stats)
        {
            if (this.NeverSell(obj.parentSheetIndex))
            {
                return (this.AddItemToInventory(obj, farmer, Game1.getFarm(), stats));
            }

            stats.RunningSellPrice += obj.sellToStorePrice();
            return true;

        }

        /**
         * Determines of a crop is on the always-sell list.
         */
        private bool AlwaysSell(int cropId)
        {
            return this.AlwaysSellLookup.Contains(cropId);
        }

        /**
         * Determines if a crop is on the never-sell list.  This does an alwaysSell lookup, because the always sell list trumps the never sell list,
         * in a case where a crop is included in both.
         */
        private bool NeverSell(int cropId)
        {
            if (this.AlwaysSellLookup.Contains(cropId))
                return false;
            else
                return this.NeverSellLookup.Contains(cropId);
        }

        /**
         * Determines if a crop in on the ignore (do-not-harvest) list.
         */
        private bool Ignore(int cropId)
        {
            return this.IgnoreLookup.Contains(cropId);
        }

        /**
         * Attempts to add the crop to the farmer's inventory.  If the crop is on the always sell list, it is sold instead.
         */
        private bool AddItemToInventory(StardewValley.Object obj, SFarmer farmer, Farm farm, ReplanterStats stats)
        {
            if (this.AlwaysSell(obj.parentSheetIndex))
            {
                return this.SellCrops(farmer, obj, stats);
            }

            bool wasAdded = false;

            if (farmer.couldInventoryAcceptThisItem(obj) && !this.BypassInventory)
            {
                farmer.addItemToInventory(obj);
                wasAdded = true;

                this.Monitor.Log("[Replanter] Was able to add item to inventory.", LogLevel.Trace);
            }
            else
            {
                StardewValley.Object chest = null;

                ChestDef preferred = null;
                this.Chests.TryGetValue(obj.ParentSheetIndex, out preferred);

                if (preferred != null)
                {
                    if (preferred.Location.Equals("house"))
                    {
                        FarmHouse house = (FarmHouse)Game1.getLocationFromName("FarmHouse");
                        house.objects.TryGetValue(preferred.Tile, out chest);
                    }
                    else
                    {
                        farm.objects.TryGetValue(preferred.Tile, out chest);
                    }

                    if (chest == null || !(chest is Chest))
                    {
                        // Try getting the default chest.
                        farm.objects.TryGetValue(this.ChestCoords, out chest);
                    }
                }
                else
                {
                    farm.objects.TryGetValue(this.ChestCoords, out chest);
                }

                if (chest != null && chest is Chest)
                {
                    Item i = ((Chest)chest).addItem(obj);
                    if (i == null)
                    {
                        wasAdded = true;
                    }
                    else
                    {
                        // If this condition was reached because bypassInventory was set, then try the inventory.
                        if (this.BypassInventory && farmer.couldInventoryAcceptThisItem(obj))
                        {
                            farmer.addItemToInventory(obj);
                            wasAdded = true;
                        }
                        else
                        {
                            this.InventoryAndChestFull = true;

                            this.Monitor.Log("Was NOT able to add items to chest.", LogLevel.Trace);
                        }
                    }

                }
                else
                {
                    this.Monitor.Log($"Did not find a chest at {(int)this.ChestCoords.X},{(int)this.ChestCoords.Y}", LogLevel.Trace);

                    // If bypassInventory is set to true, but there's no chest: try adding to the farmer's inventory.
                    if (this.BypassInventory)
                    {
                        this.Monitor.Log($"No chest at {(int)this.ChestCoords.X},{(int)this.ChestCoords.Y}, you should place a chest there, or set bypassInventory to \'false\'.", LogLevel.Trace);

                        if (farmer.couldInventoryAcceptThisItem(obj))
                        {
                            farmer.addItemToInventory(obj);
                            wasAdded = true;
                        }
                        else
                        {
                            this.InventoryAndChestFull = true;

                            this.Monitor.Log("Was NOT able to add item to inventory or a chest.  (No chest found, bypassInventory set to 'true')", LogLevel.Trace);
                        }
                    }
                    else
                    {
                        this.InventoryAndChestFull = true;

                        this.Monitor.Log("Was NOT able to add item to inventory or a chest.  (No chest found, bypassInventory set to 'false')", LogLevel.Trace);
                    }
                }
            }

            return wasAdded;
        }

        /**
         * Gets a random message from a specific list.
         */
        private string GetRandomMessage(Dictionary<int, string> messageStore)
        {
            this.Monitor.Log($"returning random message from : {messageStore.Count}", LogLevel.Trace);

            int rand = this.Random.Next(1, messageStore.Count + 1);

            string value = "...$h#$e#";

            messageStore.TryGetValue(rand, out value);

            return value;
        }

        /**
         * Loads the dialog.xnb file and sets up each of the dialog lookup files.
         */
        private void ReadInMessages()
        {
            //Dictionary<int, string> objects = Game1.content.Load<Dictionary<int, string>>("Data\\ObjectInformation");
            try
            {
                this.AllMessages = this.Content.Load<Dictionary<string, string>>("dialog");

                this.Dialogue = this.DialogueManager.GetDialog("Xdialog", this.AllMessages);
                this.Greetings = this.DialogueManager.GetDialog("greeting", this.AllMessages);
                //unfinishedMessages = this.DialogueManager.GetDialog("unfinishedmoney", allmessages);
                this.FreebieMessages = this.DialogueManager.GetDialog("freebies", this.AllMessages);
                this.InventoryMessages = this.DialogueManager.GetDialog("unfinishedinventory", this.AllMessages);
                this.Smalltalk = this.DialogueManager.GetDialog("smalltalk", this.AllMessages);

                Dictionary<int, string> characterDialog = this.DialogueManager.GetDialog(this.Checker, this.AllMessages);

                if (characterDialog.Count > 0)
                {
                    int index = this.Smalltalk.Count + 1;
                    foreach (KeyValuePair<int, string> d in characterDialog)
                    {
                        this.Smalltalk.Add(index, d.Value);
                        index++;
                    }
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Exception loading content:{ex}", LogLevel.Error);
            }
        }
    }
}
