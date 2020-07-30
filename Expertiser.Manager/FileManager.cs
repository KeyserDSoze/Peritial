using Expertiser.Base.Constant;
using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Documents;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Rystem.Data;
using Rystem.Web;
using Rystem.Web.Backoffice;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Expertiser
{
    public class FileManager
    {
        private readonly Manager Manager;
        public FileManager(Manager manager)
            => this.Manager = manager;
        public async Task<IList<string>> GetUriFiles(string fileId, string name = null)
            => await new File().SearchAsync($"{fileId}/{name}");
        public async Task DeleteFileAsync(string name)
        {
            await new File()
            {
                Name = name
            }.DeleteAsync();
        }
        public async Task<IList<string>> SaveFile(string fileId, Stream stream, string fileName, string contentType, string name = null)
        {
            if (name == null)
                name = Guid.NewGuid().ToString("N");
            else if (name.EndsWith("/"))
                name = $"{name}{Guid.NewGuid():N}";
            var upload = await new File()
            {
                Name = $"{fileId}/{name}.{fileName.Split('.').Last()}",
                Properties = new DataProperties()
                {
                    ContentType = contentType
                },
                Stream = stream
            }.WriteAsync();
            return await this.GetUriFiles(fileId);
        }
    }
}