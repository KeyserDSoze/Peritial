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
        [ModelBinder(BinderType = typeof(DecimalModelBinder))]
        public decimal Amount { get; set; }
        [ModelBinder(BinderType = typeof(DecimalModelBinder))]
        public decimal? Extra { get; set; }
        public double? Percentage { get; set; }
    }
}
