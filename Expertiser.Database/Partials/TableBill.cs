using Expertiser.Database.Binders;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableBillMetadata))]
    public partial class TableBill
    {

    }
    public class TableBillMetadata
    {
        [Display(Name = nameof(Amount))]
        [ModelBinder(BinderType = typeof(DecimalModelBinder))]
        public decimal Amount { get; set; }
        [Display(Name = nameof(Extra))]
        [ModelBinder(BinderType = typeof(DecimalModelBinder))]
        public decimal? Extra { get; set; }
        [Display(Name = nameof(Percentage))]
        [ModelBinder(BinderType = typeof(DecimalModelBinder))]
        public decimal? Percentage { get; set; }
    }
}
