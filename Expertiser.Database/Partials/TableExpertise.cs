using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableExpertiseMetadata))]
    public partial class TableExpertise
    {
        
    }
    public class TableExpertiseMetadata
    {
        [DisplayName(nameof(MemberId))]
        public string MemberId { get; set; }

        [DisplayName(nameof(ContributorId))]
        public string ContributorId { get; set; }

        [DisplayName(nameof(Status))]
        public int Status { get; set; }

        [DisplayName(nameof(BranchId))]
        public int BranchId { get; set; }

        [DisplayName(nameof(ExternalId))]
        public string ExternalId { get; set; }

        [DisplayName(nameof(AssignmentDate))]
        public DateTime? AssignmentDate { get; set; }

        [DisplayName(nameof(InspectionDate))]
        public DateTime? InspectionDate { get; set; }

        [DisplayName(nameof(Liquidated))]
        public decimal? Liquidated { get; set; }

        [DisplayName(nameof(AccidentId))]
        public int AccidentId { get; set; }
    }
}
