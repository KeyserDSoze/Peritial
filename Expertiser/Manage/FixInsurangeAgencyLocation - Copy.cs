using Expertiser.Database.Models;
using Microsoft.EntityFrameworkCore;
using Rystem.UnitTest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Manage
{
    public class FixInsurangeAgencyLocation : IUnitTest
    {
        public async Task<bool> DoWorkAsync(Action<object> action, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                List<TableCity> cities = await context.TableCity.ToListAsync();
                List<TableLocation> locations = await context.TableLocation.Include(x => x.City).ToListAsync();
                foreach (TableInsuranceAgency insuranceAgency in await context.TableInsuranceAgency.ToListAsync())
                {
                    bool checkin = false;
                    foreach (TableCity city in cities)
                    {
                        if (city.Name == "Vittoria")
                            continue;
                        if (insuranceAgency.Label.ToLower().Contains($" {city.Name.ToLower()} ") ||
                            insuranceAgency.Label.ToLower().Contains($" {city.Name.ToLower()}-") ||
                            insuranceAgency.Label.ToLower().Contains($"-{city.Name.ToLower()} ") ||
                            insuranceAgency.Label.ToLower().Contains($"-{city.Name.ToLower()}-") ||
                            insuranceAgency.Label.ToLower().StartsWith($"{city.Name.ToLower()} ") ||
                            insuranceAgency.Label.ToLower().StartsWith($"{city.Name.ToLower()}-") ||
                            insuranceAgency.Label.ToLower().Contains($"({city.Name.ToLower()})")
                            )
                        {
                            string oldLabel = insuranceAgency.Label;
                            TableLocation location = locations.Where(x => x.CityId == city.Id).FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Address));
                            if (location == null)
                            {
                                location = new TableLocation()
                                {
                                    Address = string.Empty,
                                    CityId = city.Id,
                                    PostalCode = city.PostalCodes.First(),
                                };
                                context.TableLocation.Add(location);
                                await context.SaveChangesAsync();
                                locations.Add(location);
                            }
                            insuranceAgency.AddressId = location.Id;
                            context.TableInsuranceAgency.Update(insuranceAgency);
                            await context.SaveChangesAsync();
                            Console.WriteLine($"{oldLabel} --> New Record in {city.Name}");
                            checkin = true;
                            break;
                        }
                    }
                    if (!checkin)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"{insuranceAgency.Label}");
                        Console.ResetColor();
                    }
                }
                await context.SaveChangesAsync();
            }
            return true;
        }
    }
}
