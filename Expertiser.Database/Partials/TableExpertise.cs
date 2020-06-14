using Expertiser.Database.Binders;
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
        [Display(Name = nameof(MemberId))]
        public string MemberId { get; set; }

        [Display(Name = nameof(ContributorId))]
        public string ContributorId { get; set; }

        [Display(Name = nameof(Status))]
        public int Status { get; set; }

        [Display(Name = nameof(BranchId))]
        public int BranchId { get; set; }

        [Display(Name = nameof(ExternalId))]
        public string ExternalId { get; set; }

        [Display(Name = nameof(AssignmentDate))]
        [DisplayFormat(ApplyFormatInEditMode = true, DataFormatString = "{0:dd/MM/yyyy}")]
        [DataType(DataType.Date)]
        public DateTime? AssignmentDate { get; set; }

        [Display(Name = nameof(InspectionDate))]
        [DisplayFormat(ApplyFormatInEditMode = true, DataFormatString = "{0:dd/MM/yyyy}")]
        [DataType(DataType.Date)]
        public DateTime? InspectionDate { get; set; }

        [Display(Name = nameof(Liquidated))]
        [ModelBinder(BinderType = typeof(DecimalModelBinder))]
        public decimal? Liquidated { get; set; }

        [Display(Name = nameof(AccidentId))]
        public int AccidentId { get; set; }

        [Display(Name = nameof(Accident))]
        public virtual TableAccident Accident { get; set; }

        [Display(Name = nameof(Branch))]
        public virtual TableBranch Branch { get; set; }

        [Display(Name = nameof(Contributor))]
        public virtual AspNetUsers Contributor { get; set; }

        [Display(Name = nameof(Member))]
        public virtual AspNetUsers Member { get; set; }

        [Display(Name = nameof(TableBill))]
        public virtual ICollection<TableBill> TableBill { get; set; }

        //[Display(Name = nameof(TableMessages))]
        //public virtual ICollection<TableMessages> TableMessages { get; set; }
    }
}
