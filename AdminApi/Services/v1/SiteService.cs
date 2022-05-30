﻿using Common;
using Common.DistributedLock;
using Repository.Database;
using System;
using System.Linq;

namespace AdminApi.Services.v1
{
    public class SiteService
    {


        private readonly DatabaseContext db;
        private readonly IDistributedLock distLock;
        private readonly SnowflakeHelper snowflakeHelper;

        public SiteService(DatabaseContext db, IDistributedLock distLock, SnowflakeHelper snowflakeHelper)
        {
            this.db = db;
            this.distLock = distLock;
            this.snowflakeHelper = snowflakeHelper;
        }


        public bool SetSiteInfo(string key, string? value)
        {

            if (value != null)
            {

                var appSetting = db.TAppSetting.Where(t => t.IsDelete == false && t.Module == "Site" && t.Key == key).FirstOrDefault();

                if (appSetting == null)
                {
                    appSetting = new()
                    {
                        Id = snowflakeHelper.GetId(),
                        Module = "Site",
                        Key = key,
                        Value = value,
                        CreateTime = DateTime.UtcNow
                    };
                    db.TAppSetting.Add(appSetting);
                }
                else
                {
                    appSetting.Value = value;
                }

                db.SaveChanges();
            }

            return true;
        }
    }
}
