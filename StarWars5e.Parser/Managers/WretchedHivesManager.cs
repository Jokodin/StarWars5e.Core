﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using StarWars5e.Models;
using StarWars5e.Models.EnhancedItems;
using StarWars5e.Models.Enums;
using StarWars5e.Models.Equipment;
using StarWars5e.Parser.Localization;
using StarWars5e.Parser.Processors;
using StarWars5e.Parser.Processors.PHB;
using StarWars5e.Parser.Processors.WH;
using Wolnik.Azure.TableStorage.Repository;

namespace StarWars5e.Parser.Managers
{
    public class WretchedHivesManager
    {
        private readonly ITableStorage _tableStorage;
        private readonly CloudBlobContainer _cloudBlobContainer;
        private readonly GlobalSearchTermRepository _globalSearchTermRepository;
        private readonly IBaseProcessor<ChapterRules> _wretchedHivesChapterRulesProcessor;
        private readonly WeaponPropertyProcessor _weaponPropertyProcessor;
        private readonly ArmorPropertyProcessor _armorPropertyProcessor;
        private readonly WretchedHivesEquipmentProcessor _wretchedHivesEquipmentProcessor;

        private readonly List<string> _whFilesName = new List<string>
        {
            "WH.wh_00.txt", "WH.wh_01.txt", "WH.wh_02.txt", "WH.wh_03.txt", "WH.wh_04.txt", "WH.wh_05.txt", "WH.wh_06.txt",
            "WH.wh_07.txt", "WH.wh_08.txt", "WH.wh_aa.txt", "WH.wh_changelog.txt"
        };
        private readonly ILocalization _localization;

        public WretchedHivesManager(ITableStorage tableStorage, CloudStorageAccount cloudStorageAccount,
            GlobalSearchTermRepository globalSearchTermRepository, ILocalization localization)
        {
            _tableStorage = tableStorage;
            _globalSearchTermRepository = globalSearchTermRepository;
            _localization = localization;
            _wretchedHivesEquipmentProcessor = new WretchedHivesEquipmentProcessor();
            _wretchedHivesChapterRulesProcessor = new WretchedHivesChapterRulesProcessor(globalSearchTermRepository);

            var cloudBlobClient = cloudStorageAccount.CreateCloudBlobClient();
            _cloudBlobContainer = cloudBlobClient.GetContainerReference($"wretched-hives-rules-{_localization.Language}");

            _weaponPropertyProcessor = new WeaponPropertyProcessor(ContentType.Core, _localization.WretchedHivesWeaponProperties);

            _armorPropertyProcessor = new ArmorPropertyProcessor(ContentType.Core, _localization.WretchedHivesArmorProperties);
        }

        public async Task Parse()
        {
            try
            {
                var rules = await _wretchedHivesChapterRulesProcessor.Process(_whFilesName, _localization);

                await _cloudBlobContainer.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Off, null, null);
                foreach (var chapterRules in rules)
                {
                    var json = JsonConvert.SerializeObject(chapterRules);
                    var blob = _cloudBlobContainer.GetBlockBlobReference($"{chapterRules.ChapterName}.json");

                    await blob.UploadTextAsync(json);
                }
            }
            catch (StorageException)
            {
                Console.WriteLine("Failed to upload WH rules.");
            }

            try
            {
                var enhancedItemProcessor = new EnhancedItemProcessor(_localization);
                var enhancedItems = await enhancedItemProcessor.Process(_whFilesName.Where(f => f.Equals("WH.wh_aa.txt")).ToList(), _localization);

                foreach (var enhancedItem in enhancedItems)
                {
                    enhancedItem.ContentSourceEnum = ContentSource.WH;

                    var enhancedItemSearchTerm = _globalSearchTermRepository.CreateSearchTerm(enhancedItem.Name, GlobalSearchTermType.EnhancedItem, ContentType.Core,
                        $"/loot/enhancedItems?search={enhancedItem.Name}");
                    _globalSearchTermRepository.SearchTerms.Add(enhancedItemSearchTerm);
                }

                await _tableStorage.AddBatchAsync<EnhancedItem>($"enhancedItems{_localization.Language}", enhancedItems,
                    new BatchOperationOptions { BatchInsertMethod = BatchInsertMethod.InsertOrReplace });
            }
            catch (StorageException)
            {
                Console.WriteLine("Failed to upload WH enhanced items.");
            }

            try
            {
                var equipment =
                    await _wretchedHivesEquipmentProcessor.Process(new List<string>{ "WH.wh_05.txt" }, _localization);

                foreach (var equipment1 in equipment)
                {
                    equipment1.ContentSourceEnum = ContentSource.WH;

                    switch (equipment1.EquipmentCategoryEnum)
                    {
                        case EquipmentCategory.Unknown:
                        case EquipmentCategory.Ammunition:
                        case EquipmentCategory.Explosive:
                        case EquipmentCategory.Storage:
                        case EquipmentCategory.AdventurePack:
                        case EquipmentCategory.Communications:
                        case EquipmentCategory.DataRecordingAndStorage:
                        case EquipmentCategory.LifeSupport:
                        case EquipmentCategory.Medical:
                        case EquipmentCategory.WeaponOrArmorAccessory:
                        case EquipmentCategory.Tool:
                        case EquipmentCategory.Mount:
                        case EquipmentCategory.Vehicle:
                        case EquipmentCategory.TradeGood:
                        case EquipmentCategory.Utility:
                        case EquipmentCategory.GamingSet:
                        case EquipmentCategory.MusicalInstrument:
                        case EquipmentCategory.Droid:
                        case EquipmentCategory.Clothing:
                        case EquipmentCategory.AlcoholicBeverage:
                        case EquipmentCategory.Spice:
                        case EquipmentCategory.Kit:
                            var equipmentSearchTerm = _globalSearchTermRepository.CreateSearchTerm(equipment1.Name,
                                GlobalSearchTermType.AdventuringGear, ContentType.Core,
                                $"/loot/adventuringGear/?search={equipment1.Name}");
                            _globalSearchTermRepository.SearchTerms.Add(equipmentSearchTerm);
                            break;
                        case EquipmentCategory.Weapon:
                            var weaponSearchTerm = _globalSearchTermRepository.CreateSearchTerm(equipment1.Name,
                                GlobalSearchTermType.Weapon, ContentType.Core,
                                $"/loot/weapons/?search={equipment1.Name}");
                            _globalSearchTermRepository.SearchTerms.Add(weaponSearchTerm);
                            break;
                        case EquipmentCategory.Armor:
                            var searchTermType = GlobalSearchTermType.Armor;
                            if (equipment1.ArmorClassificationEnum == ArmorClassification.Shield)
                            {
                                searchTermType = GlobalSearchTermType.Shield;
                            }

                            var armorSearchTerm = _globalSearchTermRepository.CreateSearchTerm(equipment1.Name,
                                searchTermType, ContentType.Core,
                                $"/loot/armor/?search={equipment1.Name}");
                            _globalSearchTermRepository.SearchTerms.Add(armorSearchTerm);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                var dupes = equipment
                    .GroupBy(i => i.RowKey)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key);

                await _tableStorage.AddBatchAsync<Equipment>($"equipment{_localization.Language}", equipment,
                    new BatchOperationOptions { BatchInsertMethod = BatchInsertMethod.InsertOrReplace });
            }
            catch (StorageException e)
            {
                Console.WriteLine("Failed to upload WH equipment.");
            }

            try
            {
                var weaponProperties = await _weaponPropertyProcessor.Process(new List<string> {"WH.wh_05.txt"}, _localization);

                await _tableStorage.AddBatchAsync<WeaponProperty>($"weaponProperties{_localization.Language}", weaponProperties,
                    new BatchOperationOptions { BatchInsertMethod = BatchInsertMethod.InsertOrReplace });
            }
            catch (StorageException)
            {
                Console.WriteLine("Failed to upload WH weapon properties.");
            }

            try
            {
                var armorProperties =
                    await _armorPropertyProcessor.Process(new List<string> { "WH.wh_05.txt" }, _localization);

                await _tableStorage.AddBatchAsync<ArmorProperty>($"armorProperties{_localization.Language}", armorProperties,
                    new BatchOperationOptions { BatchInsertMethod = BatchInsertMethod.InsertOrReplace });
            }
            catch (StorageException)
            {
                Console.WriteLine("Failed to upload WH armor properties.");
            }

            try
            {
                var playerHandbookFeatProcessor = new PlayerHandbookFeatProcessor(_localization);
                var feats = await playerHandbookFeatProcessor.Process(new List<string> { "WH.wh_06.txt" }, _localization);

                foreach (var feat in feats)
                {
                    feat.ContentSourceEnum = ContentSource.WH;

                    var featSearchTerm = _globalSearchTermRepository.CreateSearchTerm(feat.Name, GlobalSearchTermType.Feat, ContentType.Core,
                        $"/characters/feats/?search={feat.Name}");
                    _globalSearchTermRepository.SearchTerms.Add(featSearchTerm);
                }

                await _tableStorage.AddBatchAsync<Feat>($"feats{_localization.Language}", feats,
                    new BatchOperationOptions { BatchInsertMethod = BatchInsertMethod.InsertOrReplace });
            }
            catch (StorageException)
            {
                Console.WriteLine("Failed to upload WH feats.");
            }
        }
    }
}
