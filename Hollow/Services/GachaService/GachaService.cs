﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hollow.Core.Gacha;
using Hollow.Core.Gacha.Common;
using Hollow.Core.Gacha.Uigf;
using Hollow.Helpers;
using Hollow.Models;
using Hollow.Services.ConfigurationService;
using Serilog;

namespace Hollow.Services.GachaService;

public partial class GachaService(IConfigurationService configurationService, HttpClient httpClient) : IGachaService
{
    public Dictionary<string, GachaRecordProfile>? GachaRecordProfileDictionary { get; set; }
    
    public async Task<Dictionary<string, GachaRecordProfile>?> LoadGachaRecordProfileDictionary()
    {
        //TODO: UIGF 4.0 Schema + Strict Json Validation
        GachaRecords gachaRecords;
        if(!File.Exists(AppInfo.GachaRecordPath))
        {
            gachaRecords = new GachaRecords();
            await File.WriteAllTextAsync(AppInfo.GachaRecordPath, JsonSerializer.Serialize(gachaRecords, HollowJsonSerializer.Options));
        }
        else
        {
            gachaRecords = JsonSerializer.Deserialize<GachaRecords>(await File.ReadAllTextAsync(AppInfo.GachaRecordPath))!;
        }

        var gachaRecordProfileDictionary =  gachaRecords.Profiles.ToDictionary(item => item.Uid, item => item);
        foreach (var profile in gachaRecordProfileDictionary.Keys)
        {
            gachaRecordProfileDictionary[profile].List = gachaRecordProfileDictionary[profile].List.OrderByDescending(item => GachaAnalyser.GetTimestamp(item.Time)).ToList();
        }

        GachaRecordProfileDictionary = gachaRecordProfileDictionary;
        return gachaRecordProfileDictionary;
    }
    
    private string GetLatestTime(string uid, string gachaType)
    {
        if(GachaRecordProfileDictionary is null || !GachaRecordProfileDictionary.TryGetValue(uid, out var record))
        {
            return "0";
        }
        return record.List.Find(item => item.GachaType == gachaType)?.Time ?? "0";
    }
    
    private static (bool, List<GachaItem>) OmitExistedRecords(string time, List<GachaItem> gachaItems)
    {
        var endTimeIndex = gachaItems.FindIndex(0, item => item.Time == time);
        return endTimeIndex != -1 ? (true, gachaItems[..endTimeIndex]) : (false, gachaItems);
    }
    
    private const string GachaLogUrl = "https://public-operation-nap.mihoyo.com/common/gacha_record/api/getGachaLog?authkey_ver=1&authkey={0}&lang=zh-cn&game_biz=nap_cn&size=20&real_gacha_type={1}&end_id=";
    private readonly int[] _gachaTypes = [1, 2, 3, 5]; // 1 - standard, 2 - exclusive, 3 - w-engine, 5 - bangboo
    
    public async Task<Response<GachaRecords>> GetGachaRecords(string authKey, IProgress<Response<string>> progress)
    {
        if (!await IsAuthKeyValid(authKey))
        {
            progress.Report(new Response<string>(false, "authKey is invalid"));
            return new Response<GachaRecords>(false, "authKey is invalid");
        }
        
        var gachaRecords = new GachaRecords();
        var targetProfile = new GachaRecordProfile();
        var uid = "";
        var isCompletionMode = false;
        var fetchRecordsCount = 0;
        var newRecordsCount = 0;

        foreach (var gachaType in _gachaTypes)
        {
            var nthPage = 1;
            var pageEndId = "0";
            while (true)
            {
                var pageUrl = string.Format(GachaLogUrl, authKey, gachaType);
                if (nthPage > 1)
                {
                    pageUrl += pageEndId;
                }
                
                var page = await httpClient.GetAsync(pageUrl);
                var pageContent = await page.Content.ReadAsStringAsync();
                var pageData = JsonSerializer.Deserialize<GachaPage>(pageContent);
                if (pageData!.Data!.List.Count == 0)
                {
                    break;
                }
                var pageDataList = pageData.Data!.List;
                
                fetchRecordsCount += pageDataList.Count;
                if (nthPage == 1)
                {
                    uid = pageDataList[0].Uid;
                    targetProfile.Uid = uid;
                }
                
                // If UID exists in records, into completion
                if(GachaRecordProfileDictionary is not null && GachaRecordProfileDictionary.ContainsKey(uid))
                {
                    isCompletionMode = true;
                    var time = GetLatestTime(uid, gachaType.ToString());
                    var omitted = OmitExistedRecords(time, pageDataList);
                    if (omitted.Item1)
                    {
                        newRecordsCount += omitted.Item2.Count;
                        progress.Report(new Response<string>(true, "progress") { Data = $"{string.Join('^', omitted.Item2.Select(x => x.Name))}^{uid}^{gachaType}^{nthPage}"});
                        
                        var targetExistedGachaRecords =
                            GachaRecordProfileDictionary[uid].List.Where(item => item.GachaType == gachaType.ToString());
                        targetProfile.List.AddRange(omitted.Item2);
                        targetProfile.List.AddRange(targetExistedGachaRecords);
                        break;
                    }
                    pageDataList = omitted.Item2;
                }
                
                newRecordsCount += pageDataList.Count;
                targetProfile.List.AddRange(pageDataList);
                progress.Report(new Response<string>(true, "progress") { Data = $"{string.Join('^', pageData.Data.List.Select(x => x.Name))}^{uid}^{gachaType}^{nthPage}"});
                pageEndId = pageData.Data.List[^1].Id;
                nthPage++;
                await Task.Delay(TimeSpan.FromMilliseconds(400));
            }
            
            await Task.Delay(TimeSpan.FromMilliseconds(400));
        }

        if(isCompletionMode)
        {
            gachaRecords.Profiles.Remove(GachaRecordProfileDictionary![uid]);
        }
        gachaRecords.Profiles.Add(targetProfile);
        
        gachaRecords.Info.ExportTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        gachaRecords.Info.ExportAppVersion = AppInfo.AppVersion;
        progress.Report(new Response<string>(true, $"success {fetchRecordsCount} {newRecordsCount}"));

        return new Response<GachaRecords>(true) {Data = gachaRecords};
    }

    private async Task<bool> IsAuthKeyValid(string authKey)
    {
        var firstPage = await httpClient.GetAsync(string.Format(GachaLogUrl, authKey, _gachaTypes[0]));
        var firstPageContent = await firstPage.Content.ReadAsStringAsync();
        return !firstPageContent.Contains("\"data\": null");
    }
    
    public Response<string> GetAuthKey()
    {
        var gameDirectory = configurationService.AppConfig.Game.Directory;
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return new Response<string>(false, "gameDirectory is empty");
        }
        
        var webCachesPath = Path.Combine(gameDirectory, "ZenlessZoneZero_Data", "webCaches");
        if (!Directory.Exists(webCachesPath))
        {
            return new Response<string>(false, "webCaches not found");
        }
        
        var webCachesVersionPath = Directory.GetDirectories(webCachesPath).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(webCachesVersionPath))
        {
            return new Response<string>(false, "webCaches not found");
        }
        
        var dataPath = Path.Combine(webCachesVersionPath, "Cache", "Cache_Data", "data_2");
        if (!File.Exists(dataPath))
        {
            return new Response<string>(false, "data file not found");
        }

        var authKey = GetAuthKeyFromFile(dataPath);
        if (!authKey.IsSuccess)
        {
            return authKey;
        }

        var targetGachaLogUrl = authKey.Data.Split("&authkey=")[1].Split("&")[0];
        Log.Information("Get authKey: {0}", targetGachaLogUrl);
        return new Response<string> (true) {Data = targetGachaLogUrl};
    }

    private Response<string> GetAuthKeyFromFile(string dataPath)
    {
        using var fileStream = File.Open(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fileStream);
        var data = reader.ReadToEnd();
        var gachaLogUrls = GachaLogUrlRegex().Matches(data);
        if (gachaLogUrls.Count == 0)
        {
            return new Response<string>(false, "authKey not found");
        }

        return new Response<string>(true) { Data = gachaLogUrls[^1].Value };
    }

    [GeneratedRegex(@"https://public-operation-nap.mihoyo.com/common/gacha_record/api/getGachaLog[-a-zA-Z0-9+&@#/%?=~_|!:,.;]*[-a-zA-Z0-9+&@#/%=~_|]end_id=")]
    private static partial Regex GachaLogUrlRegex();
}