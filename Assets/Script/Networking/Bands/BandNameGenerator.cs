using System;

namespace YARG.Networking.Bands
{
    /// <summary>
    /// Generates fun random band names in the format "[Adjective] [Noun]".
    /// Examples: "Electric Gorillas", "Flying Flamingos", "Mad Moms"
    /// </summary>
    public static class BandNameGenerator
    {
        private static readonly string[] Adjectives =
        {
            // Energy/Power
            "Electric", "Thunder", "Blazing", "Raging", "Atomic", "Turbo", "Hyper", "Ultra",
            "Mega", "Super", "Sonic", "Volcanic", "Nuclear", "Plasma", "Lightning",
            
            // Movement/Action
            "Flying", "Soaring", "Screaming", "Spinning", "Rolling", "Charging", "Leaping",
            "Dancing", "Rocking", "Shredding", "Jamming",
            
            // Mood/Attitude
            "Mad", "Wild", "Savage", "Furious", "Radical", "Funky", "Groovy", "Chill",
            "Sneaky", "Sassy", "Spicy", "Salty", "Sweet", "Bitter", "Grumpy", "Happy",
            
            // Style/Aesthetic
            "Cosmic", "Neon", "Velvet", "Crystal", "Shadow", "Golden", "Iron", "Silver",
            "Midnight", "Sunset", "Starlight", "Moonlit", "Crimson", "Azure", "Violet",
            
            // Genre vibes
            "Jazzy", "Punk", "Metal", "Acoustic", "Digital", "Analog", "Vintage", "Retro",
            
            // Cool/Epic
            "Epic", "Legendary", "Mystic", "Noble", "Royal", "Elite", "Supreme", "Ultimate",
            "Fearless", "Mighty", "Glorious", "Victorious",
            
            // Temperature
            "Frozen", "Icy", "Fiery", "Molten", "Steamy",
            
            // Misc fun
            "Lucky", "Chaotic", "Silent", "Loud", "Tiny", "Giant", "Ancient", "Future"
        };

        private static readonly string[] Nouns =
        {
            // Animals - Primates
            "Gorillas", "Monkeys", "Chimps", "Baboons",
            
            // Animals - Birds
            "Flamingos", "Eagles", "Falcons", "Hawks", "Owls", "Crows", "Ravens",
            "Penguins", "Parrots", "Peacocks", "Pelicans",
            
            // Animals - Big Cats & Canines
            "Tigers", "Lions", "Panthers", "Leopards", "Jaguars",
            "Wolves", "Foxes", "Huskies", "Coyotes",
            
            // Animals - Bears & Large
            "Bears", "Pandas", "Koalas", "Grizzlies",
            "Elephants", "Rhinos", "Hippos", "Gorillas",
            
            // Animals - Sea
            "Sharks", "Dolphins", "Whales", "Octopi", "Squids", "Jellyfish",
            
            // Animals - Reptiles
            "Dragons", "Cobras", "Vipers", "Geckos", "Crocs", "Raptors",
            
            // Animals - Cute/Funny
            "Llamas", "Alpacas", "Sloths", "Otters", "Badgers", "Raccoons",
            "Wombats", "Kangaroos", "Hamsters", "Ferrets", "Hedgehogs",
            "Kittens", "Puppies", "Bunnies", "Ducklings",
            
            // Mythical
            "Unicorns", "Phoenix", "Griffins", "Hydras", "Krakens", "Yetis",
            
            // People/Characters
            "Ninjas", "Pirates", "Vikings", "Wizards", "Knights", "Samurai",
            "Moms", "Dads", "Grandmas", "Grandpas", "Rockstars", "Legends",
            
            // Robots/Sci-Fi
            "Robots", "Cyborgs", "Androids", "Mechs", "Drones",
            
            // Food (funny)
            "Pickles", "Tacos", "Burritos", "Waffles", "Pancakes", "Nachos",
            "Pretzels", "Nuggets", "Noodles", "Meatballs",
            
            // Objects (funny)
            "Toasters", "Blenders", "Banjos", "Kazoos", "Tambourines",
            "Spatulas", "Crowbars", "Rockets", "Comets", "Asteroids"
        };

        private static readonly Random _sharedRandom = new();
        private static readonly object _lock = new();

        /// <summary>
        /// Generates a random band name.
        /// </summary>
        /// <returns>A band name like "Electric Gorillas"</returns>
        public static string Generate()
        {
            lock (_lock)
            {
                var adjective = Adjectives[_sharedRandom.Next(Adjectives.Length)];
                var noun = Nouns[_sharedRandom.Next(Nouns.Length)];
                return $"{adjective} {noun}";
            }
        }

        /// <summary>
        /// Generates a deterministic band name based on a seed.
        /// Useful for ensuring all clients generate the same name for a band.
        /// </summary>
        /// <param name="seed">Seed value (e.g., bandId combined with lobby seed)</param>
        /// <returns>A band name like "Electric Gorillas"</returns>
        public static string Generate(int seed)
        {
            var random = new Random(seed);
            var adjective = Adjectives[random.Next(Adjectives.Length)];
            var noun = Nouns[random.Next(Nouns.Length)];
            return $"{adjective} {noun}";
        }

        /// <summary>
        /// Generates a deterministic band name based on multiple seed components.
        /// </summary>
        /// <param name="lobbySeed">The lobby's random seed (shared by all clients)</param>
        /// <param name="bandId">The band's unique ID</param>
        /// <param name="regenerationCount">How many times the name has been regenerated (0 for first)</param>
        /// <returns>A band name like "Electric Gorillas"</returns>
        public static string Generate(int lobbySeed, int bandId, int regenerationCount = 0)
        {
            // Use a deterministic hash instead of HashCode.Combine which varies between processes
            // Simple but effective: multiply, add, XOR with prime numbers
            int combinedSeed = DeterministicHash(lobbySeed, bandId, regenerationCount);
            return Generate(combinedSeed);
        }
        
        /// <summary>
        /// Creates a deterministic hash from multiple integer values.
        /// Unlike HashCode.Combine(), this produces the same result across all processes/machines.
        /// </summary>
        private static int DeterministicHash(int a, int b, int c)
        {
            // Use a simple but effective mixing function with prime multipliers
            // This ensures the same inputs always produce the same output
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + a;
                hash = hash * 31 + b;
                hash = hash * 31 + c;
                return hash;
            }
        }

        /// <summary>
        /// Gets the total number of possible unique combinations.
        /// </summary>
        public static int TotalCombinations => Adjectives.Length * Nouns.Length;

        /// <summary>
        /// Gets the number of available adjectives.
        /// </summary>
        public static int AdjectiveCount => Adjectives.Length;

        /// <summary>
        /// Gets the number of available nouns.
        /// </summary>
        public static int NounCount => Nouns.Length;
    }
}
