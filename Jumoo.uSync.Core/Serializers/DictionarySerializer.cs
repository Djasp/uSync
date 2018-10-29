﻿using Jumoo.uSync.Core.Extensions;
using Jumoo.uSync.Core.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Services;

namespace Jumoo.uSync.Core.Serializers
{
    public class DictionarySerializer : SyncBaseSerializer<IDictionaryItem>, ISyncChangeDetail
    {
        IPackagingService _packagingService;
        ILocalizationService _localizationService;
        ILogger _logger;

        public override string SerializerType { get { return uSyncConstants.Serailization.Dictionary; } }


        public DictionarySerializer() :
            base (Constants.Packaging.DictionaryItemNodeName)
        {
            _packagingService = ApplicationContext.Current.Services.PackagingService;
            _localizationService = ApplicationContext.Current.Services.LocalizationService;
            _logger = ApplicationContext.Current.ProfilingLogger.Logger;
        }

        public DictionarySerializer(string itemType) : base(itemType)
        {
            _packagingService = ApplicationContext.Current.Services.PackagingService;
            _localizationService = ApplicationContext.Current.Services.LocalizationService;
            _logger = ApplicationContext.Current.ProfilingLogger.Logger;
        }

        internal override SyncAttempt<IDictionaryItem> DeserializeCore(XElement node)
        {
            // var items = _packagingService.ImportDictionaryItems(node);
            // var item = items.LastOrDefault();

            var langs = _localizationService.GetAllLanguages().ToList();
            var item = UpdateDictionaryValues(node, null, langs); 
            return SyncAttempt<IDictionaryItem>.SucceedIf(
                item != null, 
                item != null ? item.ItemKey : node.NameFromNode(),
                item,
                ChangeType.Import);
        }

        private IDictionaryItem UpdateDictionaryValues(XElement node, Guid? parent, List<ILanguage> languages)
        {
            var itemKeyNode = node.Attribute("Key");
            if (itemKeyNode == null)
                return null;

            var itemKey = itemKeyNode.Value;
            

            IDictionaryItem item = default(IDictionaryItem);

            /*
             
            // currently (v7.5.3) you can't set the key value of a dictionary item
            // so we dont put it in the export file. and don't look up on it
             
            Guid guid = Guid.Empty;
            
            var itemGuid = node.Attribute("Guid");
            if (itemGuid != null && Guid.TryParse(itemGuid.Value, out guid))
            {
                item = _localizationService.GetDictionaryItemById(guid);
            }
            */

            // we need to know what version this becomes a viable option
            //
            var guid = node.Attribute("guid").ValueOrDefault(Guid.Empty);
            if (guid != Guid.Empty)
            {
                item = _localizationService.GetDictionaryItemById(guid);
            }

            if (item == null && _localizationService.DictionaryItemExists(itemKey))
            {
                item = _localizationService.GetDictionaryItemByKey(itemKey);
            }


            // both create by guid or key haven't found the value
            if (item == null)
            {
                if (parent.HasValue)
                    item = new DictionaryItem(parent.Value, itemKey);
                else
                {
                    item = new DictionaryItem(itemKey);
                    
                    // We can do this for new ones, and then renames later down the 
                    // line will work. 
                    if (guid != Guid.Empty) { item.Key = guid; }
                }
            }

            if (guid != Guid.Empty && guid != item.Key)
            {
                // (< at least 7.13) this causes SQL errors down the line, because of conflicts in other tables 
                // item.Key = guid;
                // LogHelper.Debug<DictionarySerializer>("Set the Guid of the Dictionary from {0} to {1}", () => item.Key, () => guid);
            }

            if (item.ItemKey != itemKey)
            {
                item.ItemKey = itemKey;
            }

            foreach (var valueNode in node.Elements("Value"))
            {
                var languageId = valueNode.Attribute("LanguageCultureAlias").ValueOrDefault("");
                if (!string.IsNullOrEmpty(languageId))
                {
                    var language = languages.FirstOrDefault(x => x.IsoCode == languageId);
                    if (language != null)
                    {
                        _localizationService.AddOrUpdateDictionaryValue(item, language, valueNode.Value);
                    }
                }
            }

            _localizationService.Save(item);

            // children
            var childNames = new List<string>();
            foreach (var child in node.Elements("DictionaryItem"))
            {
                UpdateDictionaryValues(child, item.Key, languages);

                var childName = child.Attribute("Key").ValueOrDefault(String.Empty);
                if (!string.IsNullOrEmpty(childName))
                    childNames.Add(childName);
            }

            // Clean children - when they are removed from the source. 
            /* 
             * This is a true sync - but for dicrionary items it can be destructive, so we leave 
             * the orphans this can mean a rename gets missed, but its better that then us deleting
             * half the content edited dicrionary items. 
             */

            // var children = _localizationService.GetDictionaryItemChildren(item.Key);
            // CleanChildren(childNames, children);
            

            return item;
        }

        private void CleanChildren(IEnumerable<string> names, IEnumerable<IDictionaryItem> children)
        {
            if (children == null || !children.Any()) return;


            var missing = children.Where(x => !names.InvariantContains(x.ItemKey));
            if (missing != null && missing.Any())
            {
                _logger.Debug<DictionarySerializer>("Cleaning up orphaned children items");

                foreach (var item in missing)
                {
                    _logger.Debug<DictionarySerializer>("Deleting: {0}", () => item.ItemKey);
                    _localizationService.Delete(item);
                }
            }
        }

        internal override SyncAttempt<XElement> SerializeCore(IDictionaryItem item)
        {
            /*
            var node = _packagingService.Export(item, true);
            */
            var xml = GetDictionaryElement(item);

            return SyncAttempt<XElement>.SucceedIf(
                xml != null,
                xml != null ? item.ItemKey : xml.NameFromNode(),
                xml,
                typeof(IDictionaryItem),
                ChangeType.Export);
        }

        private XElement GetDictionaryElement(IDictionaryItem item, bool top = true)
        {
            var node = new XElement(Constants.Packaging.DictionaryItemNodeName,
                new XAttribute("Key", item.ItemKey));

            if (top)
                node.Add(new XAttribute("guid", item.Key));

            foreach (var translation in item.Translations.OrderBy(x => x.Language.IsoCode))
            {
                node.Add(new XElement("Value",
                    new XAttribute("LanguageId", translation.LanguageId),
                    new XAttribute("LanguageCultureAlias", translation.Language.IsoCode),
                    new XCData(translation.Value)));
            }

            var children = _localizationService
                .GetDictionaryItemChildren(item.Key)
                .ToList();

            foreach (var child in children.OrderBy(x => x.ItemKey))
            {
                node.Add(GetDictionaryElement(child, false));
            }

            return node; 

        }

        public override bool IsUpdate(XElement node)
        {
            if (node.IsArchiveFile())
                return false;

            var nodeHash = node.GetSyncHash();
            if (string.IsNullOrEmpty(nodeHash))
                return true;

            var key = node.Attribute("Key");
            if (key == null)
                return true;

            var item = _localizationService.GetDictionaryItemByKey(key.Value);
            if (item == null)
                return true;

            var attempt = Serialize(item);
            if (!attempt.Success)
                return true;

            var itemHash = attempt.Item.GetSyncHash();

            return (!nodeHash.Equals(itemHash));
        }

        #region ISyncChangeDetail : Support for detailed change reports
        public IEnumerable<uSyncChange> GetChanges(XElement node)
        {
            var nodeHash = node.GetSyncHash();
            if (string.IsNullOrEmpty(nodeHash))
                return null;

            var key = node.Attribute("Key");
            if (key == null)
                return null;

            var item = _localizationService.GetDictionaryItemByKey(key.Value);
            if (item == null)
                return uSyncChangeTracker.NewItem(key.Value);

            var attempt = Serialize(item);
            if (attempt.Success)
            {
                return uSyncChangeTracker.GetChanges(node, attempt.Item, "");
            }
            else
            {
                return uSyncChangeTracker.ChangeError(key.Value);
            }
        }
        #endregion

    }
}
