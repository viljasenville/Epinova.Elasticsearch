﻿using System;
using System.Linq;
using System.Web.Mvc;
using Epinova.ElasticSearch.Core.Admin;
using Epinova.ElasticSearch.Core.Contracts;
using Epinova.ElasticSearch.Core.EPiServer.Controllers.Abstractions;
using Epinova.ElasticSearch.Core.EPiServer.Models.ViewModels;
using Epinova.ElasticSearch.Core.Models;
using Epinova.ElasticSearch.Core.Models.Admin;
using Epinova.ElasticSearch.Core.Settings;
using Epinova.ElasticSearch.Core.Settings.Configuration;
using Epinova.ElasticSearch.Core.Utilities;
using EPiServer.DataAbstraction;
using EPiServer.Scheduler;

namespace Epinova.ElasticSearch.Core.EPiServer.Controllers
{
    public class ElasticAdminController : ElasticSearchControllerBase
    {
        private readonly ICoreIndexer _coreIndexer;
        private readonly IElasticSearchSettings _settings;
        private readonly Health _healthHelper;
        private readonly IHttpClientHelper _httpClientHelper;
        private readonly IScheduledJobRepository _scheduledJobRepository;
        private readonly IScheduledJobExecutor _scheduledJobExecutor;

        public ElasticAdminController(
            ILanguageBranchRepository languageBranchRepository,
            ICoreIndexer coreIndexer,
            IElasticSearchSettings settings,
            IHttpClientHelper httpClientHelper,
            IScheduledJobRepository scheduledJobRepository,
            IScheduledJobExecutor scheduledJobExecutor)
            : base(settings, httpClientHelper, languageBranchRepository)
        {
            _coreIndexer = coreIndexer;
            _settings = settings;
            _healthHelper = new Health(settings, httpClientHelper);
            _httpClientHelper = httpClientHelper;
            _scheduledJobRepository = scheduledJobRepository;
            _scheduledJobExecutor = scheduledJobExecutor;
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult Index()
        {
            HealthInformation clusterHealth = _healthHelper.GetClusterHealth();
            Node[] nodeInfo = _healthHelper.GetNodeInfo();

            var adminViewModel = new AdminViewModel(clusterHealth, Indices.OrderBy(i => i.Type), nodeInfo);

            return View("~/Views/ElasticSearchAdmin/Admin/Index.cshtml", adminViewModel);
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult RunIndexJob()
        {
            var indexJob = _scheduledJobRepository.List().FirstOrDefault(job => job.Name == Constants.IndexEPiServerContentDisplayName);
            if(indexJob != null)
            {
                _scheduledJobExecutor.StartAsync(indexJob);
            }
            return RedirectToAction("Index");
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult AddNewIndex()
        {
            if(Core.Server.Info.Version.Major < 5)
            {
                throw new InvalidOperationException("Elasticsearch version 5 or higher required");
            }

            var config = ElasticSearchSection.GetConfiguration();

            foreach(var lang in Languages)
            {
                foreach(IndexConfiguration indexConfig in config.IndicesParsed)
                {
                    var indexName = _settings.GetCustomIndexName(indexConfig.Name, lang.Key);
                    Type indexType = GetIndexType(indexConfig, config);

                    var index = new Index(_settings, _httpClientHelper, indexName);

                    if(!index.Exists)
                    {
                        index.Initialize(indexType);
                    }

                    if(IsCustomType(indexType))
                    {
                        _coreIndexer.UpdateMapping(indexType, indexType, indexName, lang.Key, false);
                        index.WaitForStatus();
                    }
                    else if(_settings.CommerceEnabled)
                    {
                        CreateCommerceIndex(lang.Key, indexConfig, indexType);
                    }
                    else
                    {
                        index.WaitForStatus();
                        index.DisableDynamicMapping(indexType);
                        index.WaitForStatus();
                    }
                }
            }

            return RedirectToAction("Index");
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult DeleteIndex(string indexName)
        {
            var indexing = new Indexing(_settings, _httpClientHelper);
            indexing.DeleteIndex(indexName);

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult DeleteAll()
        {
            var indexing = new Indexing(_settings, _httpClientHelper);

            foreach(var index in Indices)
            {
                indexing.DeleteIndex(index.Index);
            }

            return RedirectToAction("Index");
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult ChangeTokenizer(string indexName, string tokenizer)
        {
            var indexing = new Indexing(_settings, _httpClientHelper);
            var index = new Index(_settings, _httpClientHelper, indexName);

            indexing.Close(indexName);
            index.ChangeTokenizer(tokenizer);
            indexing.Open(indexName);

            index.WaitForStatus();

            return RedirectToAction("Index");
        }

        private static bool IsCustomType(Type indexType)
            => indexType != null && indexType != typeof(IndexItem);

        private static Type GetIndexType(IndexConfiguration index, ElasticSearchSection config)
        {
            if(index.Default || config.IndicesParsed.Count() == 1)
            {
                return typeof(IndexItem);
            }

            if(String.IsNullOrWhiteSpace(index.Type))
            {
                return null;
            }

            return Type.GetType(index.Type, false, true);
        }

        private void CreateCommerceIndex(string language, IndexConfiguration indexConfig, Type indexType)
        {
            var indexName = _settings.GetCustomIndexName($"{indexConfig.Name}-{Constants.CommerceProviderName}", language);
            var index = new Index(_settings, _httpClientHelper, indexName);
            if(!index.Exists)
            {
                index.Initialize(indexType);
                index.WaitForStatus();
                index.DisableDynamicMapping(indexType);
                index.WaitForStatus();
            }
        }
    }
}
