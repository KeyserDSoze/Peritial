using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableAccidentMetadata))]
    [BindProperties]
    public partial class TableAccident
    {
    }
    public class TableAccidentMetadata
    {
        [Display(Name = nameof(AddressId))]
        public int AddressId { get; set; }

        [Display(Name = nameof(InsuranceId))]
        public int InsuranceId { get; set; }

        [Display(Name = nameof(InsuranceAgencyId))]
        public int InsuranceAgencyId { get; set; }

        [Display(Name = nameof(InspectorateId))]
        public int InspectorateId { get; set; }

        [Display(Name = nameof(ExternalId))]
        public string ExternalId { get; set; }

        [Display(Name = nameof(AccidentDate))]
        [DisplayFormat(ApplyFormatInEditMode = true, DataFormatString = "{0:dd/MM/yyyy}")]
        [DataType(DataType.Date)]
        public DateTime AccidentDate { get; set; }

        [Display(Name = nameof(DenunciationDate))]
        [DisplayFormat(ApplyFormatInEditMode = true, DataFormatString = "{0:dd/MM/yyyy}")]
        [DataType(DataType.Date)]
        public DateTime? DenunciationDate { get; set; }

        [Display(Name = nameof(Inspectorate))]
        public virtual TableInspectorate Inspectorate { get; set; }

        [Display(Name = nameof(Insurance))]
        public virtual TableInsurance Insurance { get; set; }

        [Display(Name = nameof(InsuranceAgency))]
        public virtual TableInsuranceAgency InsuranceAgency { get; set; }

        [Display(Name = nameof(Place))]
        public virtual TableLocation Place { get; set; }

        [Display(Name = nameof(TableExpertise))]
        public virtual ICollection<TableExpertise> TableExpertise { get; set; }
    }
}
