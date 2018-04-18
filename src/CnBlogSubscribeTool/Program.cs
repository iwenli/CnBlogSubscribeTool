using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CnBlogSubscribeTool.Config;
using HtmlAgilityPack;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using Polly;
using Polly.Retry;

namespace CnBlogSubscribeTool
{
    class Program
    {
        private static readonly Stopwatch Sw = new Stopwatch();
        private static readonly List<BlogSource> BlogSourceList = new List<BlogSource>();
        private static Logger _logger;
        private static Logger _sendLogger;
        private static MailConfig _mailConfig;
        private static string _baseDir;
        private static string _baseDataPath;
        private static RetryPolicy _retryTwoTimesPolicy;
        static void Main(string[] args)
        {
            Init();

            //work thread
            new Thread(new ThreadStart(WorkStart)).Start();

            Console.Title = "Blogs Article Archives Tool";
            Console.WriteLine("Service Working...");
            // SendMailTest();
            Console.Read();
        }

        static void Init()
        {

            //初始化重试器
            _retryTwoTimesPolicy =
                Policy
                    .Handle<Exception>()
                    .Retry(3, (ex, count) =>
                    {
                        _logger.Error("Excuted Failed! Retry {0}", count);
                        _logger.Error("Exeption from {0}", ex.GetType().Name);
                    });

            //获取应用程序所在目录
            Type type = (new Program()).GetType();
            _baseDir = Path.GetDirectoryName(type.Assembly.Location);
            //设置数据存储目录
            _baseDataPath = Path.Combine(_baseDir, "Data");
            if (!Directory.Exists(_baseDataPath))
            {
                Directory.CreateDirectory(_baseDataPath);
            }

            //检查工作目录
            foreach (var source in BlogSourceList)
            {
                if (!Directory.Exists(Path.Combine(_baseDataPath, source.Path)))
                {
                    Directory.CreateDirectory(Path.Combine(_baseDataPath, source.Path));
                }
            }

            //初始化日志
            LogManager.Configuration = new XmlLoggingConfiguration(Path.Combine(_baseDir, "Config", "NLog.Config"));
            _logger = LogManager.GetLogger("Global");
            _sendLogger = LogManager.GetLogger("MailSend");

            //初始化邮件配置
            _mailConfig =
                JsonConvert.DeserializeObject<MailConfig>(
                    File.ReadAllText(Path.Combine(_baseDir, "Config", "Mail.json")));
            //加载数据 优先缓存
            LoadData();

        }
        //持久化本次抓取数据到文本 以便于异常退出恢复之后不出现重复数据
        private static void SaveData()
        {
            var _tmpFilePath = Path.Combine(_baseDataPath, $"cache.tmp");
            File.WriteAllText(_tmpFilePath, JsonConvert.SerializeObject(BlogSourceList));
        }
        //加载数据,首先从缓存中读取
        private static void LoadData()
        {
            var _tmpFilePath = Path.Combine(_baseDataPath, $"cache.tmp");
            if (File.Exists(_tmpFilePath))
            {
                try
                {
                    var data = File.ReadAllText(_tmpFilePath);
                    var res = JsonConvert.DeserializeObject<List<BlogSource>>(data);
                    if (res != null && res.Count > 0)
                    {
                        BlogSourceList.AddRange(res);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("缓存数据加载失败，本次将弃用！详情:" + e.Message);
                    File.Delete(_tmpFilePath);
                }
            }
            if (BlogSourceList.Count > 0)
            {
                return;
            }
            var source1 = new BlogSource()
            {
                Name = "博客园",
                BlogDataUrl = "https://www.cnblogs.com/",
                FileName = "cnblogs",
                Path = "Blogs",
                DicXPath = new Dictionary<string, string>(),
                RecordTime = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 9, 0, 0),
                PreviousBlogs = new List<Blog>()
            };
            source1.DicXPath["item"] = "//div[@class='post_item_body']";
            source1.DicXPath["title"] = "h3/a";
            source1.DicXPath["summary"] = "p[@class='post_item_summary']";
            source1.DicXPath["foot"] = "div[@class='post_item_foot']";
            source1.DicXPath["author"] = "a";
            BlogSourceList.Add(source1);

            //产品经理
            var source2 = new BlogSource()
            {
                Name = "人人都是产品经理",
                BlogDataUrl = "http://www.woshipm.com/",
                FileName = "woshipm",
                Path = "WoshiPm",
                DicXPath = new Dictionary<string, string>(),
                RecordTime = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 17, 0, 0),
                PreviousBlogs = new List<Blog>()
            };
            source2.DicXPath["item"] = "//div[@class='postlist-item u-clearfix']";
            source2.DicXPath["title"] = "div/h2/a";
            source2.DicXPath["summary"] = "div/p[@class='des']";
            source2.DicXPath["foot"] = "div/div[@class='stream-list-meta']";
            source2.DicXPath["author"] = "span[@class='author']/a";
            source2.DicXPath["date"] = "time";
            BlogSourceList.Add(source2);
        }

        static void WorkStart()
        {
            try
            {
                while (true)
                {
                    _retryTwoTimesPolicy.Execute(Work);
                    //每五分钟执行一次
                    Thread.Sleep(5 * 60 * 1000);
                }

            }
            catch (Exception e)
            {
                _logger.Error($"Excuted Failed,Message: ({e.Message})");

            }
        }
        /// <summary>
        /// 抓取数据入口
        /// </summary>
        static void Work()
        {

            foreach (var source in BlogSourceList)
            {
                Work(source);
            }
        }
        /// <summary>
        /// 抓取博客园
        /// </summary>
        static void Work(BlogSource source)
        {
            try
            {
                Sw.Reset();
                Sw.Start();

                //重复数量统计
                int repeatCount = 0;

                string html = HttpUtil.GetString(source.BlogDataUrl);

                List<Blog> blogs = new List<Blog>();

                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(html);
                //获取所有文章数据项
                var itemBodys = doc.DocumentNode.SelectNodes(source.DicXPath["item"]);

                foreach (var itemBody in itemBodys)
                {
                    //标题元素
                    var titleElem = itemBody.SelectSingleNode(source.DicXPath["title"]);
                    //获取标题
                    var title = titleElem?.InnerText;
                    //获取url
                    var url = titleElem?.Attributes["href"]?.Value;

                    //摘要元素
                    var summaryElem = itemBody.SelectSingleNode(source.DicXPath["summary"]);
                    //获取摘要
                    var summary = summaryElem?.InnerText.Replace("\r\n", "").Trim();

                    //数据项底部元素
                    var footElem = itemBody.SelectSingleNode(source.DicXPath["foot"]);
                    //获取作者
                    var author = footElem?.SelectSingleNode(source.DicXPath["author"])?.InnerText;
                    //获取文章发布时间
                    var publishTime = (source.Path == "WoshiPm") ?
                    footElem?.SelectSingleNode(source.DicXPath["date"])?.InnerText :
                    Regex.Match(footElem?.InnerText, "\\d+-\\d+-\\d+ \\d+:\\d+").Value;

                    //组装博客对象
                    Blog blog = new Blog()
                    {
                        Title = title,
                        Url = url,
                        Summary = summary,
                        Author = author,
                        PublishTime = DateTime.Parse(publishTime)
                    };
                    blogs.Add(blog);
                }

                string blogFileName = $"{source.FileName}-{DateTime.Now:yyyy-MM-dd}.txt";
                string blogFilePath = Path.Combine(_baseDataPath, source.Path, blogFileName);
                FileStream fs = new FileStream(blogFilePath, FileMode.Append, FileAccess.Write);

                StreamWriter sw = new StreamWriter(fs, Encoding.UTF8);
                //去重
                foreach (var blog in blogs)
                {
                    if (source.PreviousBlogs.Any(b => b.Url == blog.Url))
                    {
                        repeatCount++;
                    }
                    else
                    {
                        sw.WriteLine($"标题：{blog.Title}");
                        sw.WriteLine($"网址：{blog.Url}");
                        sw.WriteLine($"摘要：{blog.Summary}");
                        sw.WriteLine($"作者：{blog.Author}");
                        sw.WriteLine($"发布时间：{blog.PublishTime:yyyy-MM-dd HH:mm}");
                        sw.WriteLine("--------------华丽的分割线---------------");
                    }

                }
                sw.Close();
                fs.Close();

                //清除上一次抓取数据记录
                source.PreviousBlogs.Clear();
                //加入本次抓取记录
                source.PreviousBlogs.AddRange(blogs);

                Sw.Stop();

                //统计信息
                _logger.Info($"Get {source.Name} data success,Time:{Sw.ElapsedMilliseconds}ms,Data Count:{blogs.Count},Repeat:{repeatCount},Effective:{blogs.Count - repeatCount}");

                //发送邮件
                if ((DateTime.Now - source.RecordTime).TotalHours >= 24)
                {
                    _sendLogger.Info($"准备发送{source.Name}聚合邮件，记录时间:{source.RecordTime:yyyy-MM-dd HH:mm:ss}");
                    SendMail(source);
                    source.RecordTime = source.RecordTime.AddDays(1);
                    _sendLogger.Info($"{source.Name}记录时间已更新:{source.RecordTime:yyyy-MM-dd HH:mm:ss}");
                }
                SaveData();
            }
            catch (Exception ex)
            {
                System.Console.WriteLine(ex.Message);
                throw;
            }
            finally
            {
                Sw.Stop();
            }
        }

        /// <summary>
        /// 发送邮件
        /// </summary>
        static void SendMail(BlogSource source)
        {
            string blogFileName = $"{source.FileName}-{source.RecordTime:yyyy-MM-dd}.txt";
            string blogFilePath = Path.Combine(_baseDataPath, source.Path, blogFileName);

            if (!File.Exists(blogFilePath))
            {
                _sendLogger.Error("未发现文件记录，无法发送邮件，所需文件名：" + blogFileName);
                return;
            }
            //邮件正文
            string mailContent = "";
            FileStream mailFs = new FileStream(blogFilePath, FileMode.Open, FileAccess.Read);
            StreamReader sr = new StreamReader(mailFs, Encoding.UTF8);
            while (!sr.EndOfStream)
            {
                mailContent += sr.ReadLine() + "<br/>";
            }
            sr.Close();
            mailFs.Close();

            //附件内容
            string blogFileContent = File.ReadAllText(blogFilePath);

            //发送邮件
            MailUtil.SendMail(_mailConfig, _mailConfig.ReceiveList, "王薇",
                $"{source.Name}首页文章聚合-{source.RecordTime:yyyy-MM-dd}", mailContent, Encoding.UTF8.GetBytes(blogFileContent),
                blogFileName);

            _sendLogger.Info($"{blogFileName},文件已发送");
        }
    }
}
