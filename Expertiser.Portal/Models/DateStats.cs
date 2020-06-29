using Microsoft.OData.Edm;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace Expertiser.Portal.Models
{
    public class DateStats
    {
        [Display(Name = nameof(From))]
        public DateTime From { get; set; }
        [Display(Name = nameof(To))]
        public DateTime To { get; set; }
    }
}