using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace MyFirstMafAgent
{
    internal class Tools
    {
        [Description("Get the weather for a location.")]
        static string GetWeather(
            [Description("The city to look up.")] string location)
            => $"The weather in {location} is sunny.";

       
    }
}
