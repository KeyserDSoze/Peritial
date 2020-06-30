using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableInsuranceAgencyMetadata))]
    public partial class TableInsuranceAgency
    {
    }
    public partial class TableInsuranceAgencyMetadata
    {
        [Display(Name = nameof(Label))]
        public string Label { get; set; }
        [Display(Name = nameof(ExternalId))]
        public string ExternalId { get; set; }
        [Display(Name = nameof(InsuranceCompanyId))]
        public int InsuranceCompanyId { get; set; }
        [Display(Name = nameof(AddressId))]
        public int? AddressId { get; set; }
        [Display(Name = nameof(IsActive))]
        public bool IsActive { get; set; }
    }
}