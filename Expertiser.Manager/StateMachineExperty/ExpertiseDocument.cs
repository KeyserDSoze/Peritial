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
    public enum DocumentType
    {
        Document,
        Image
    }
    public class ExpertiseDocument
    {
        public string Name { get; }
        public StatusType MinimumStatus { get; }
        public StatusType ForceToStatus { get; }
        public DocumentType Type { get; }
        private ExpertiseDocument(string name, StatusType statusType, StatusType forceToStatus, DocumentType type)
        {
            this.Name = name;
            this.MinimumStatus = statusType;
            this.Type = type;
            this.ForceToStatus = forceToStatus;
        }
        public static List<ExpertiseDocument> Instance { get; } = new List<ExpertiseDocument>()
        {
            new ExpertiseDocument("AccidentDocs/", StatusType.Registered, StatusType.Registered, DocumentType.Document),
            new ExpertiseDocument("Photos/", StatusType.Registered, StatusType.Registered,DocumentType.Image),
            new ExpertiseDocument("RawExpertise", StatusType.Assigned, StatusType.Registered, DocumentType.Document),
            new ExpertiseDocument("CorrectedExpertise", StatusType.ToBeRevised, StatusType.Registered, DocumentType.Document),
            new ExpertiseDocument("CompletedExpertise", StatusType.Assigned, StatusType.Revised, DocumentType.Document),
            new ExpertiseDocument("ReservedNote", StatusType.Assigned, StatusType.Registered, DocumentType.Document),
        };
    }
}