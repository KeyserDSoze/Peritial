using Expertiser.Database.Binders;
using Expertiser.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableExpertiseMetadata))]
    public partial class TableExpertise
    {
        [NotMapped]
        public StatusType StatusType => (StatusType)this.Status;
        [NotMapped]
        public string ConcreteId => $"{this.Accident?.YearlyId}{this.Accident?.InsuranceAgency?.InsuranceCompany?.Alias}{this.Accident?.AccidentDate:yyyy}{this.Member?.Alias}{this.Contributor?.Alias ?? this.Member?.Alias}";
        [NotMapped]
        public decimal? TotalPercentage => this.TableBill?.Where(x => x.IsAuthorized).Sum(x => (x.Amount * x.Percentage / 100) + x.Extra);
        [NotMapped]
        public decimal? TotalExtra => this.TableBill?.Where(x => x.IsAuthorized).Sum(x => x.Extra);
        [NotMapped]
        public decimal? TotalBill => this.TableBill?.Where(x => x.IsAuthorized).Sum(x => x.Amount);
        [NotMapped]
        public decimal? TotalMember => TotalBill - TotalPercentage;
        [NotMapped]
        public decimal? TotalPercentageNoExtra => TotalPercentage - TotalExtra;
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
