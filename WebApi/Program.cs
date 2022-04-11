﻿using Medallion.Threading;
using Medallion.Threading.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using WebApi.Filters;
using WebApi.Libraries;
using WebApi.Libraries.Swagger;
using WebApi.Libraries.Verify;
using WebApi.Models.AppSetting;

namespace WebApi
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Common.EnvironmentHelper.ChangeDirectory(args);
            Common.EnvironmentHelper.InitTestServer();

            var builder = WebApplication.CreateBuilder(args);

            //启用 Kestrel Https 并绑定证书
            //builder.WebHost.UseKestrel(options =>
            //{
            //    options.ConfigureHttpsDefaults(options =>
            //    {
            //        options.ServerCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(Path.Combine(AppContext.BaseDirectory, "xxxx.pfx"), "123456");
            //    });
            //});
            //builder.WebHost.UseUrls("https://*");

            // Add services to the container.

            //为各数据库注入连接字符串
            Repository.Database.DatabaseContext.ConnectionString = builder.Configuration.GetConnectionString("dbConnection");
            builder.Services.AddDbContextPool<Repository.Database.DatabaseContext>(options => { }, 100);


            builder.Services.AddSingleton<IDistributedLockProvider>(new SqlDistributedSynchronizationProvider(builder.Configuration.GetConnectionString("dbConnection")));
            builder.Services.AddSingleton<IDistributedSemaphoreProvider>(new SqlDistributedSynchronizationProvider(builder.Configuration.GetConnectionString("dbConnection")));
            builder.Services.AddSingleton<IDistributedUpgradeableReaderWriterLockProvider>(new SqlDistributedSynchronizationProvider(builder.Configuration.GetConnectionString("dbConnection")));



            builder.Services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = long.MaxValue;
            });

            builder.Services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            builder.Services.Configure<IISServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            builder.Services.AddHsts(options =>
            {
                options.MaxAge = TimeSpan.FromDays(365);
            });


            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                //options.KnownProxies.Add(IPAddress.Parse("10.0.0.100"));
            });

            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
            });


            builder.Services.AddControllers();


            //注册JWT认证机制
            var jwtSetting = builder.Configuration.GetSection("JWTSetting").Get<JWTSetting>();
            var issuerSigningKey = ECDsa.Create();
            issuerSigningKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(jwtSetting.PublicKey), out int i);
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwtSetting.Issuer,
                    ValidAudience = jwtSetting.Audience,
                    IssuerSigningKey = new ECDsaSecurityKey(issuerSigningKey)
                };
            });


            builder.Services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(context => IdentityVerification.Authorization(context)).Build();
            });


            //注册HttpContext
            builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();


            //注册全局过滤器
            builder.Services.AddMvc(options => options.Filters.Add(new GlobalFilter()));


            //注册跨域信息
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("cors", policy =>
                {
                    policy.SetIsOriginAllowed(origin => true)
                       .AllowAnyHeader()
                       .AllowAnyMethod()
                       .AllowCredentials();
                });
            });


            builder.Services.AddControllers().AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new Common.Json.DateTimeConverter());
                options.JsonSerializerOptions.Converters.Add(new Common.Json.DateTimeNullConverter());
                options.JsonSerializerOptions.Converters.Add(new Common.Json.DateTimeOffsetConverter());
                options.JsonSerializerOptions.Converters.Add(new Common.Json.DateTimeOffsetNullConverter());
                options.JsonSerializerOptions.Converters.Add(new Common.Json.LongConverter());
            });




            builder.Services.AddApiVersioning(options =>
            {
                //通过Header向客户端通报支持的版本
                options.ReportApiVersions = true;

                //允许不加版本标记直接调用接口
                options.AssumeDefaultVersionWhenUnspecified = true;

                //接口默认版本
                //options.DefaultApiVersion = new ApiVersion(1, 0);

                //如果未加版本标记默认以当前最高版本进行处理
                options.ApiVersionSelector = new CurrentImplementationApiVersionSelector(options);

                options.ApiVersionReader = new HeaderApiVersionReader("api-version");
                //options.ApiVersionReader = new QueryStringApiVersionReader("api-version");
            });


            builder.Services.AddVersionedApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });


            builder.Services.AddTransient<IConfigureOptions<SwaggerGenOptions>, SwaggerConfigureOptions>();


            //注册Swagger生成器，定义一个和多个Swagger 文档
            builder.Services.AddSwaggerGen(options =>
            {
                options.OperationFilter<SwaggerOperationFilter>();

                options.MapType<long>(() => new OpenApiSchema { Type = "string", Format = "long" });


                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml"), true);


                //开启 Swagger JWT 鉴权模块
                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme()
                {
                    Description = "在下框中输入请求头中需要添加Jwt授权Token：Bearer Token",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    BearerFormat = "JWT",
                    Scheme = "Bearer"
                });
                options.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer"
                                }
                            },
                        Array.Empty<string>()
                    }
                });
            });



            //注册统一模型验证
            builder.Services.Configure<ApiBehaviorOptions>(options =>
            {
                options.InvalidModelStateResponseFactory = actionContext =>
                {

                    //获取验证失败的模型字段 
                    var errors = actionContext.ModelState.Where(e => e.Value?.Errors.Count > 0).Select(e => e.Value?.Errors.First().ErrorMessage).ToList();

                    var dataStr = string.Join(" | ", errors);

                    //设置返回内容
                    var result = new
                    {
                        errMsg = dataStr
                    };

                    return new BadRequestObjectResult(result);
                };
            });


            //注册雪花ID算法示例
            builder.Services.AddSingleton(new Common.SnowflakeHelper(0, 0));


            //注册缓存服务 内存模式
            builder.Services.AddDistributedMemoryCache();


            //注册缓存服务 SqlServer模式
            //builder.Services.AddDistributedSqlServerCache(options =>
            //{
            //    options.ConnectionString = builder.Configuration.GetConnectionString("dbConnection");
            //    options.SchemaName = "dbo";
            //    options.TableName = "t_cache";
            //});


            //注册缓存服务 Redis模式
            //builder.Services.AddStackExchangeRedisCache(options =>
            //{
            //    options.Configuration = builder.Configuration.GetConnectionString("redisConnection");
            //    options.InstanceName = "cache";
            //});

            builder.Services.AddScoped<Libraries.Http.HttpSignHandler>();

            builder.Services.AddHttpClient("", options =>
            {
                options.DefaultRequestVersion = new Version("2.0");
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });


            builder.Services.AddHttpClient("SkipSsl", options =>
            {
                options.DefaultRequestVersion = new Version("2.0");
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; }
            });


            builder.Services.AddHttpClient("HttpSign", options =>
            {
                options.DefaultRequestVersion = new Version("2.0");
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            }).AddHttpMessageHandler(t => t.GetRequiredService<Libraries.Http.HttpSignHandler>()); ;


            var app = builder.Build();

            ServiceProvider = app.Services;

            app.UseForwardedHeaders();

            app.UseResponseCompression();

            //开启倒带模式运行多次读取HttpContext.Body中的内容
            app.Use(async (context, next) =>
            {
                context.Request.EnableBuffering();
                await next.Invoke();
            });

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                //注册全局异常处理机制
                app.UseExceptionHandler(builder => builder.Run(async context => await GlobalError.ErrorEvent(context)));
            }

            app.UseHsts();


            //注册跨域信息
            app.UseCors("cors");

            //强制重定向到Https
            app.UseHttpsRedirection();


            app.UseRouting();

            //注册用户认证机制,必须放在 UseCors UseRouting 之后
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();


            //启用中间件服务生成Swagger作为JSON端点
            app.UseSwagger();

            //启用中间件服务对swagger-ui，指定Swagger JSON端点
            app.UseSwaggerUI(options =>
            {
                var apiVersionDescriptionProvider = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();
                foreach (var description in apiVersionDescriptionProvider.ApiVersionDescriptions)
                {
                    options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json", description.GroupName.ToUpperInvariant());
                }

                options.RoutePrefix = "swagger";
            });



            app.Run();

        }




        public static IServiceProvider ServiceProvider { get; set; }


    }
}
