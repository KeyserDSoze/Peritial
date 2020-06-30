using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableInsuranceMetadata))]
    public partial class TableInsurance
    {

    }
    public partial class TableInsuranceMetadata
    {
        [Display(Name = nameof(CustomerId))]
        public int CustomerId { get; set; }
        [Display(Name = nameof(ExternalId))]
        public string ExternalId { get; set; }
        [Display(Name = nameof(InsuranceCompanyId))]
        public int InsuranceCompanyId { get; set; }
    }
}
