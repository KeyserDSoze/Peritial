using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Expertiser.Database.Models
{
    [ModelMetadataType(typeof(TableLocationMetadata))]
    public partial class TableLocation 
    {
    }
    public partial class TableLocationMetadata
    {
        [Display(Name = nameof(Address))]
        public string Address { get; set; }
        [Display(Name = nameof(CityId))]
        public int CityId { get; set; }
        [Display(Name = nameof(PostalCode))]
        public string PostalCode { get; set; }
    }
}