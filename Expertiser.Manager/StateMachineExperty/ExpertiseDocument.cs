using Expertiser.Database.Models;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser
{
    public class ExpertiseDocument
    {
        public string Name { get; }
        public StatusType MinimumStatus { get; }
        private ExpertiseDocument(string name, StatusType statusType)
        {
            this.Name = name;
            this.MinimumStatus = statusType;
        }
        public static List<ExpertiseDocument> Instance { get; } = new List<ExpertiseDocument>()
        {
            new ExpertiseDocument("AccidentDocs/", StatusType.Registered),
            new ExpertiseDocument("Photos/", StatusType.Registered),
            new ExpertiseDocument("RawExpertise", StatusType.Assigned),
            new ExpertiseDocument("CorrectedExpertise", StatusType.ToBeRevised),
            new ExpertiseDocument("CompletedExpertise", StatusType.Revised),
            new ExpertiseDocument("ReservedNote", StatusType.Revised),
        };
    }
}