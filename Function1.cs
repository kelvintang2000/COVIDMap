using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml;
using BingMapsRESTToolkit;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.File;

namespace COVIDMap
{
    public static class Function1
    {

        [FunctionName("GetCOVIDMap")]
        
        public static void Run([TimerTrigger("0 1 * * * *")]TimerInfo myTimer, ILogger log)
        // remark for 
        //public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest httpreq, ILogger log)
        {
            log.LogInformation($"Start COVIDMap function executed at: {DateTime.Now}");

            //Download csv file
            #region Download csv file
            // Residential buildings in which probable/confirmed cases have resided in the past 14 days or non-residential building with 2 or more probable/confirmed cases in the past 14 days (English) 
            string _serviceUrl = "http://www.chp.gov.hk/files/misc/building_list_eng.csv";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_serviceUrl);
            req.KeepAlive = false;
            req.ProtocolVersion = HttpVersion.Version10;
            HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
            #endregion

            //Parse csv file and remove header
            #region Parse csv file
            List<BuildingEntity> records = new List<BuildingEntity>();
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                var reader = new CsvReader(sr, System.Globalization.CultureInfo.CurrentCulture);

                while (reader.Read())
                {
                    var District = reader.GetField<string>(0);
                    var BuildingName = reader.GetField<string>(1);
                    var Lastdate = reader.GetField<string>(2);
                    var RelatedInfo = reader.GetField<string>(3);

                    // Split the Related Info into separate case
                    string[] L1values = RelatedInfo.Split(',');
                    foreach (string L2value in L1values)
                    {
                        // Due to some data did not separate by comma, it separate by space
                        string[] L3values = L2value.Trim().Split(' ');

                        if (L3values.Length == 1)
                        {
                            var record = new BuildingEntity();
                            record.District = District;
                            record.BuildingName = BuildingName;
                            record.Lastdate = Lastdate;
                            record.RelatedInfo = L2value.Trim();
                            records.Add(record);
                        }
                        else
                        {
                            foreach (string L4value in L3values)
                            {
                                var record = new BuildingEntity();
                                record.District = District;
                                record.BuildingName = BuildingName;
                                record.Lastdate = Lastdate;
                                record.RelatedInfo = L4value.Trim();
                                records.Add(record);
                            }
                        }
                    }
                }
            }
            records.RemoveAt(0);
            records.RemoveAt(0);
            records.RemoveAt(0);
            log.LogInformation($"No of records: {records.Count()}");
            #endregion

            ////Use ogcio api to search address
            //#region Use ogcio api to search address
            //for (int i = 0; i < records.Count(); i++)
            //{
            //    try
            //    {
            //        string postUrl = "https://www.als.ogcio.gov.hk/lookup?n=1&q=" + System.Web.HttpUtility.UrlEncode(records[i].BuildingName + ", " + records[i].District + ", Hong Kong");

            //        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(postUrl);
            //        HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            //        using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            //        {
            //            var responseXml = reader.ReadToEnd();

            //            XmlDocument xmlDoc = new XmlDocument();
            //            xmlDoc.LoadXml(responseXml);
            //            string xpath = "AddressLookupResult/SuggestedAddress/Address/PremisesAddress/GeospatialInformation";
            //            var nodes = xmlDoc.SelectNodes(xpath);

            //            foreach (XmlNode childrenNode in nodes)
            //            {
            //                var lat = childrenNode.SelectSingleNode(".//Latitude").InnerText;
            //                var lng = childrenNode.SelectSingleNode(".//Longitude").InnerText;
            //                var findStatus = "N";
                            
            //                // Check if the locaiton not in Hong Kong
            //                if (Convert.ToDouble(lat) > 22 && Convert.ToDouble(lat) < 23)
            //                    if (Convert.ToDouble(lng) > 113 && Convert.ToDouble(lat) < 115)
            //                    {
            //                        records[i].Latitude = lat;
            //                        records[i].Longitude = lng;
            //                        findStatus = "Y";
            //                    }
            //                log.LogInformation($" {i} {records[i].BuildingName} - {findStatus} - Lat: {lat} / Long: {lng}");
            //            }

            //        }
            //    }
            //    catch (Exception ex)
            //    {
            //        log.LogError($" {i} {records[i].BuildingName} " + ex.Message.ToString());
            //    }
            //}
            //#endregion

            // Generate CSV file
            #region Generate CSV file
            string filename = "building.csv";
            var ogCsvFile = Path.Combine(Path.GetTempPath(), filename);

            using (StreamWriter sw = new StreamWriter(ogCsvFile))
            using (CsvWriter cw = new CsvWriter(sw, System.Globalization.CultureInfo.CurrentCulture))
            {
                cw.WriteHeader<BuildingEntity>();
                cw.NextRecord();
                foreach (BuildingEntity record in records)
                {
                    cw.WriteRecord<BuildingEntity>(record);
                    cw.NextRecord();
                }
            }
            #endregion

            //Upload blob storage
            #region Upload blob storage
            string AzureFileConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            try
            {
                CloudStorageAccount fileStorageAccount = CloudStorageAccount.Parse(AzureFileConnectionString);
                CloudFileClient fileClient = fileStorageAccount.CreateCloudFileClient();
                CloudFileShare share = fileClient.GetShareReference("csvfile");
                CloudFileDirectory rootDir = share.GetRootDirectoryReference();
                CloudFileDirectory fileDir = rootDir.GetDirectoryReference("building");
                fileDir.GetFileReference(filename).UploadFromFileAsync(ogCsvFile).GetAwaiter().GetResult();
                log.LogInformation($"Upload Success to Azure Blob storage at: {DateTime.Now}");
            }
            catch (Exception ex)
            {
                log.LogError("Azure Blob Exception message: " + ex.Message.ToString() + " " + DateTime.Now);
            }
            #endregion

            log.LogInformation($"Finish COVIDMap function executed at: {DateTime.Now}");

            //return (ActionResult)new OkObjectResult($"Finish COVIDMap function executed at: {DateTime.Now}");
        }

    }
}
