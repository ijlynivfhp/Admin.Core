﻿using Admin.Core.Common.Auth;
using Admin.Core.Common.BaseModel;
using Admin.Core.Common.Configs;
using Admin.Core.Model.Admin;
using FreeSql;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Admin.Core.Repository
{
    public static class IdleBusExtesions
    {
        /// <summary>
        /// 获得FreeSql实例
        /// </summary>
        /// <param name="ib"></param>
        /// <param name="serviceProvider"></param>
        /// <returns></returns>
        public static IFreeSql GetFreeSql(this IdleBus<IFreeSql> ib, IServiceProvider serviceProvider)
        {
            var user = serviceProvider.GetRequiredService<IUser>();
            var appConfig = serviceProvider.GetRequiredService<AppConfig>();

            if (appConfig.Tenant && user.DataIsolationType == DataIsolationType.OwnDb)
            {
                return RegisterAndGetFreeSql(ib, user, appConfig, serviceProvider);
            }
            else
            {
                var freeSql = serviceProvider.GetRequiredService<IFreeSql>();
                return freeSql;
            }
        }

        /// <summary>
        /// 注册并获得FreeSql实例
        /// </summary>
        /// <param name="user"></param>
        /// <param name="appConfig"></param>
        /// <param name="serviceProvider"></param>
        /// <returns></returns>
        private static IFreeSql RegisterAndGetFreeSql(IdleBus<IFreeSql> ib, IUser user, AppConfig appConfig, IServiceProvider serviceProvider)
        {
            var tenantName = "tenant_" + user.TenantId?.ToString();
            var exists = ib.Exists(tenantName);
            if (!exists)
            {
                var dbConfig = serviceProvider.GetRequiredService<DbConfig>();
                //查询租户数据库信息
                var freeSql = serviceProvider.GetRequiredService<IFreeSql>();
                var tenantRepository = freeSql.GetRepository<TenantEntity>();
                var tenant = tenantRepository.Select.DisableGlobalFilter("Tenant").WhereDynamic(user.TenantId).ToOne(a => new { a.DbType, a.ConnectionString, a.IdleTime });

                var timeSpan = tenant.IdleTime.HasValue && tenant.IdleTime.Value > 0 ? TimeSpan.FromMinutes(tenant.IdleTime.Value) : TimeSpan.MaxValue;
                ib.TryRegister(tenantName, () =>
                {
                    var freeSqlBuilder = new FreeSqlBuilder()
                        .UseConnectionString(tenant.DbType.Value, tenant.ConnectionString)
                        .UseAutoSyncStructure(false)
                        .UseLazyLoading(false)
                        .UseNoneCommandParameter(true);

                    #region 监听所有命令

                    if (dbConfig.MonitorCommand)
                    {
                        freeSqlBuilder.UseMonitorCommand(cmd => { }, (cmd, traceLog) =>
                        {
                            Console.WriteLine($"{cmd.CommandText}\r\n");
                        });
                    }

                    #endregion 监听所有命令

                    var fsql = freeSqlBuilder.Build();
                    fsql.GlobalFilter.Apply<IEntitySoftDelete>("SoftDelete", a => a.IsDeleted == false);

                    //配置实体
                    DbHelper.ConfigEntity(fsql, appConfig);

                    #region 监听Curd操作

                    if (dbConfig.Curd)
                    {
                        fsql.Aop.CurdBefore += (s, e) =>
                        {
                            Console.WriteLine($"{e.Sql}\r\n");
                        };
                    }

                    #endregion 监听Curd操作

                    #region 审计数据

                    //计算服务器时间
                    var serverTime = fsql.Select<DualEntity>().Limit(1).First(a => DateTime.UtcNow);
                    var timeOffset = DateTime.UtcNow.Subtract(serverTime);
                    fsql.Aop.AuditValue += (s, e) =>
                    {
                        DbHelper.AuditValue(e, timeOffset, user);
                    };

                    #endregion 审计数据

                    return fsql;
                }, timeSpan);
            }

            return ib.Get(tenantName);
        }
    }
}