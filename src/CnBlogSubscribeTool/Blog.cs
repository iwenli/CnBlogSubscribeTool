using System;
using System.Collections.Generic;

namespace CnBlogSubscribeTool
{
    public class BlogSource
    {
        public string Name { set; get; }
        public string Path { set; get; }
        public string FileName { set; get; }
        public DateTime RecordTime { set; get; }
        public string BlogDataUrl { set; get; }

        public Dictionary<string, string> DicXPath { set; get;}
        public List<Blog> PreviousBlogs { set; get; }
    }

    public class Blog
    {
        /// <summary>
        /// 标题
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// 博文url
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// 摘要
        /// </summary>
        public string Summary { get; set; }

        /// <summary>
        /// 作者
        /// </summary>
        public string Author { get; set; }

        /// <summary>
        /// 发布时间
        /// </summary>
        public DateTime PublishTime { get; set; }
    }
}