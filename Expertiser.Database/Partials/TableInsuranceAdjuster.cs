using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableInsuranceAdjusterMetadata))]
    public partial class TableInsuranceAdjuster
    {

    }
    public partial class TableInsuranceAdjusterMetadata
    {
        [Display(Name = nameof(Name))]
        public string Name { get; set; }
    }
}