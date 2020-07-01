using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableCustomerMetadata))]
    [BindProperties]
    public partial class TableCustomer
    {
    }
    public partial class TableCustomerMetadata
    {
        [Display(Name = nameof(FiscalCode))]
        [RegularExpression("^([A-Za-z]{6}[0-9lmnpqrstuvLMNPQRSTUV]{2}[abcdehlmprstABCDEHLMPRST]{1}[0-9lmnpqrstuvLMNPQRSTUV]{2}[A-Za-z]{1}[0-9lmnpqrstuvLMNPQRSTUV]{3}[A-Za-z]{1})|([0-9]{11})$", ErrorMessage = "Invalid Fiscal Code")]
        public string FiscalCode { get; set; }
        [Display(Name = nameof(Name))]
        public string Name { get; set; }
        [Display(Name = nameof(AddressId))]
        public int AddressId { get; set; }
    }
}
