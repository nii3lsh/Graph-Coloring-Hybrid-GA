﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Serialization.Formatters.Binary;
using System.Diagnostics;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using MathNet.Numerics.Statistics;

namespace EC_Practicum_2
{
    class Experiment
    {
        Random rnd = new Random();
        public int ColorsCount { get; set; }
        public int GraphSize { get; set; }
        public int PopulationSize { get; set; }
        private Random _random = new Random();
        public Graph[] CurrentPopulation { get; set; }
        public Graph[] OriginalPopulation { get; set; }
        public int BestFitness = int.MaxValue;
        private object _lock = new object();
        private List<double> avgs = new List<double>();
        private List<double> bst = new List<double>();

        //Measurements
        public double VdslCount = 0;
        public double GenerationCount = 0;

        private List<Tuple<int, int>> _connections;

        public Experiment(int k, string graphInputPath, int populationSize, string name, int graphSize = 450)
        {
            ColorsCount = k;
            PopulationSize = populationSize;
            GraphSize = graphSize;

            //Parse Graph text file
            _connections = new List<Tuple<int, int>>();
            var lines = File.ReadAllLines(graphInputPath);
            foreach (string line in lines)
            {
                if (line[0] == 'e')
                {
                    var split = line.Split(' ');
                    _connections.Add(new Tuple<int, int>((Int32.Parse(split[1]) - 1), (Int32.Parse(split[2]) - 1)));
                }
            }

            //initialze all individuals of population.
            CurrentPopulation = new Graph[populationSize];
            OriginalPopulation = new Graph[populationSize]; //NEED THE ORIGINAL POPULATION FOR CROSSOVER COV. COR.

            
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < PopulationSize; i++)
            {
                var tmp = new Graph(_connections, graphSize, k);
                int j = i;
                OriginalPopulation[j] = tmp.Clone() as Graph; //p1.Clone() as Graph;
                var t = Task.Run(() =>
                {
                    VDSL(tmp);
                    CurrentPopulation[j] = tmp;
                });
                tasks.Add(t);
            }

            Task.WaitAll(tasks.ToArray());

            Console.WriteLine("Init of " + name + " done..");
        }

        public void ShufflePopulation()
        {
            // Knuth shuffle algorithm :: courtesy of Wikipedia :)
            for (int t = 0; t < CurrentPopulation.Length; t++)
            {
                var tmp = CurrentPopulation[t];
                int r = rnd.Next(t, CurrentPopulation.Length);
                CurrentPopulation[t] = CurrentPopulation[r];
                CurrentPopulation[r] = tmp;
            }
        }

        private class Match
        {
            public Graph G1 { get; set; }
            public int G1Conflicts { get; set; }
            public Graph G2 { get; set; }
            public int G2Conflicts { get; set; }
        }

        public IEnumerable<Graph> GetNewGeneration(Crossover crossover)
        {
            var newPopulation = new BlockingCollection<Graph>();
            //shuffle current population
            ShufflePopulation();

            var startgen = DateTime.Now;
            var q = new List<Match>();

            var _cache = new ConcurrentDictionary<Graph, int>();

            for (int i = 0; i < PopulationSize; i += 2)
            {
                var match = new Match()
                {
                    G1 = CurrentPopulation[i],
                    G1Conflicts = _cache[CurrentPopulation[i]] = CurrentPopulation[i].GetConflicts(),
                    G2 = CurrentPopulation[i + 1],
                    G2Conflicts = _cache[CurrentPopulation[i + 1]] = CurrentPopulation[i + 1].GetConflicts()
                };
                q.Add(match);
            }

            int size = GraphSize;
            int cc = ColorsCount;

            CalcFitnessCorrelationCoefficient();

            Parallel.ForEach(q, x =>
            {
                Graph t1, t2 = null;

                t1 = CrossoverGPX(x.G1, x.G2, size, cc);
                t2 = CrossoverGPX(x.G1, x.G2, size, cc);


                var t1x = Task.Run(() =>
                {
                    VDSL(t1);
                });

                var t2x = Task.Run(() =>
                {
                    VDSL(t2);
                });

                Task.WaitAll(new[] {
                    t1x, t2x
                });

                var conflictst1 = t1.GetConflicts();
                var conflictst2 = t2.GetConflicts();

                _cache[t1] = conflictst1;
                _cache[t2] = conflictst2;

                //sort parents
                var parents = new List<Tuple<Graph, int>>
                    {
                        new Tuple<Graph, int>(x.G1,x.G1Conflicts),
                        new Tuple<Graph, int>(x.G2,x.G2Conflicts)
                    };


                parents.Sort((v, y) => -1 * y.Item2.CompareTo(v.Item2));

                //sort children
                var children = new List<Tuple<Graph, int>>
                    {
                        new Tuple<Graph, int>(t1,conflictst1),
                        new Tuple<Graph, int>(t2,conflictst2)
                    };
                children.Sort((v, y) => -1 * y.Item2.CompareTo(v.Item2));

                var winners = new Graph[2];

                for (int j = 0; j < 2; j++)
                {
                    if (children[0].Item2 > parents[0].Item2)
                    {
                        winners[j] = parents[0].Item1;
                        parents.Remove(parents[0]);
                    }
                    else
                    {
                        winners[j] = children[0].Item1;
                        children.Remove(children[0]);
                    }
                }

                var conflicts = _cache[winners[0]];
                if (conflicts < BestFitness) BestFitness = conflicts;

                newPopulation.Add(winners[0]);
                newPopulation.Add(winners[1]);

            });
            var avg = GetAverageFitness();
            Console.WriteLine("Best fintess " + BestFitness);
            Console.WriteLine("AVG Fitness: " + avg);
            avgs.Add(avg);
            bst.Add(BestFitness);

            //Flush on every 100 generations;
            if (avgs.Count % 100 == 0)
            {
                File.AppendAllText("avgs.json", JsonConvert.SerializeObject(avgs));
                File.AppendAllText("bst.json", JsonConvert.SerializeObject(bst));
                avgs.Clear();
                bst.Clear();
            }
            return newPopulation;
        }

        public Graph CrossoverSplit(Graph g1, Graph g2)
        {
            var c = new Graph(_connections, GraphSize, ColorsCount);

            var item = _random.Next(0, g1.Count);
            for (int i = 0; i < g1.Count; i++)
            {
                if (i < item)
                    c.Color(c[i], g1[i].Color);
                else
                    c.Color(c[i], g2[i].Color);

            }

            return c;
        }

        public Graph CrossoverGPX(Graph p1, Graph p2, int size, int colors)
        {
            var _p1 = p1.Clone() as Graph;
            var _p2 = p2.Clone() as Graph;

            var child = new Graph(_connections, size, colors);
            var rand = new Random();

            var currentParent = _p1;
            int i = 1;

            while (currentParent.Count > 0)
            {
                //get greatest cluster from parent
                var greatestCluster = new List<Graph.Vertex>();

                greatestCluster = currentParent.GetGreatestColorCluster();

                var crntClr = i;
                if (i > ColorsCount)
                {
                    crntClr = rand.Next(1, ColorsCount + 1);
                }

                foreach (var vertex in greatestCluster)
                {
                    child[vertex.Node].Color = crntClr;
                    _p1.Remove(vertex);
                    _p2.Remove(vertex);
                }

                if (currentParent == _p1) currentParent = _p2;
                else currentParent = _p1;
                i++;
            }
            return child;
        }

        public void Run()
        {
            var watch = Stopwatch.StartNew();

            //very bad fitness
            var crossMethod = Crossover.GPX;

            List<double> v = new List<double>();

            while (BestFitness != 0)
            {
                CurrentPopulation = GetNewGeneration(crossMethod).ToArray();
                var variance = ComputeVariance(CurrentPopulation);
                v.Add(variance);
                if (v.Count % 1000 == 0)
                {
                    File.AppendAllText("vari.json", JsonConvert.SerializeObject(variance));
                    v.Clear();
                }
                Console.WriteLine("Variance at for new generation : " + variance);
                GenerationCount++;
            }
            watch.Stop();
            var elapsedMs = watch.ElapsedMilliseconds;
            Console.WriteLine("Solution found for " + ColorsCount);
            Console.WriteLine("------------------------------------");
            Console.WriteLine("cputime: " + elapsedMs);
            Console.WriteLine("vdslcnt: " + VdslCount);
            Console.WriteLine("gencnt:" + GenerationCount);
            Console.WriteLine("------------------------------------");

        }

        private double ComputeVariance(Graph[] currentPopulation)
        {
            //cache conficts to avoid recomputing it below
            var conflicts = currentPopulation.Select(x => x.GetConflicts())
                                             .Select((x, i) => new { index = i, value = x })
                                             .ToDictionary(i => i.index, v => v.value);

            var pop = currentPopulation.Select((x, i) => new { ind = i, val = conflicts[i] * conflicts[i] });
            var mean = pop.Sum(v => conflicts[v.ind]) / currentPopulation.Length;

            //compute variance
            return pop.Sum(x => x.val) / mean;
        }


        //check if valid solution found, if so decline k, if not continue..
        /// <summary>
        /// Vertex descent local search
        /// </summary>
        /// <param name="g">The graph on which to perform the search</param>
        public IEnumerable<int> VDSL(Graph g)
        {
            var noImprovement = 0;
            var iterCount = 0;

            while (noImprovement < 20)
            {
                var oldFitness = g.GetConflicts();

                //O(n) local search, set the color of each v to the least frequent color of its neigbors
                for (int i = 0; i < g.Count; i++)
                {
                    var vertex = g[i];
                    var clrcnt = new int[g.ColorCtn + 1];
                    clrcnt[0] = int.MaxValue; //because clr 0 does not exist and I dont want to -1 first everything and then reverse this... =)
                    foreach (var neighbor in vertex.Edges)
                    {
                        var clr = g[neighbor].Color;
                        clrcnt[clr]++;
                    }


                    g.Color(vertex, Array.IndexOf(clrcnt, clrcnt.Min())); //set to colour of the least frequent color of the neighbors (optimal 0)


                }

                var conflicts = g.GetConflicts();
                if (oldFitness <= conflicts) { noImprovement++; }
                else
                {
                    noImprovement = 0;
                    iterCount = 0;
                    if (iterCount > 15)
                    {
                        Console.WriteLine("improvement after: " + iterCount);
                    }
                }
                lock (_lock)
                {
                    VdslCount++;
                    iterCount++;
                }
            }
            return g.GetConfiguration();
        }

        public int GetAverageFitness()
        {
            double total = 0;
            for (int i = 0; i < PopulationSize; i++)
            {
                total = total + CurrentPopulation[i].GetConflicts();
            }
            return (int)(total / PopulationSize);
        }

        //new average function for fitnesscorrelationcoeficient
        public int _GetAverageFitness(Graph[] _population)
        {
            double total = 0;
            for (int i = 0; i < PopulationSize; i++)
            {
                total = total + _population[i].GetConflicts();
            }
            return (int)(total / PopulationSize);
        }

        public void Shuffle(Graph g)
        {
            var random = new Random();
            var n = g.Count;
            while (n > 1)
            {
                n--;
                var i = random.Next(n + 1);
                var temp = g[i];
                g[i] = g[n];
                g[n] = temp;
            }
        }

        public void CalcFitnessCorrelationCoefficient()
        {
            List<double> parentFitnessNOVDLS    = new List<double>();
            List<double> parentFitnessVDLS      = new List<double>();      
            List<double> childrenFitnessNOVDLS  = new List<double>();
            List<double> childrenFitnessVDLS    = new List<double>();

            Console.WriteLine("CalcFitnessCorrelationCoefficient");
            Console.WriteLine("--------------------------------------");           
        
            foreach (Graph p in CurrentPopulation) //individual fitness parent with vdls
            {
                parentFitnessVDLS.Add((double)p.GetConflicts());
            }

            foreach (Graph p in OriginalPopulation) //individual fitnessparent with novdls
            {
                parentFitnessNOVDLS.Add((double)p.GetConflicts());
            }

            var childPopulationNOVDLS = new Graph[PopulationSize];          
            for (int i = 0; i < PopulationSize; i += 2) //generate all children
            {
                var c1 = CrossoverGPX(CurrentPopulation[i], CurrentPopulation[i + 1], GraphSize, ColorsCount);
                var c2 = CrossoverGPX(CurrentPopulation[i], CurrentPopulation[i + 1], GraphSize, ColorsCount);
                childPopulationNOVDLS[i]        = c1;
                childPopulationNOVDLS[i + 1]    = c2;
            }

            foreach (Graph c in childPopulationNOVDLS) //individual fitnesschildren with novdls
            {
                childrenFitnessNOVDLS.Add((double)c.GetConflicts());
            }

            var childPopulationVDLS = childPopulationNOVDLS;
            for (int i = 0; i < PopulationSize; i++) {
                VDSL(childPopulationVDLS[i]); 
            }

            foreach (Graph c in childPopulationVDLS) //individual fitnesschildren with vdls
            {
                childrenFitnessVDLS.Add((double)c.GetConflicts());
            }

            //Covariance parent child NOVDLS
            var cov_pnovdsl_cnovdsl = Statistics.Covariance(parentFitnessNOVDLS, childrenFitnessNOVDLS);
            //Covariance parent child VDLS
            var cov_pvdls_cvdls = Statistics.Covariance(parentFitnessVDLS, childrenFitnessVDLS);


            var pandc = cov_pvdls_cvdls / (Statistics.Variance(parentFitnessVDLS) * Statistics.Variance(childrenFitnessVDLS));
            var nonc = cov_pnovdsl_cnovdsl / (Statistics.Variance(parentFitnessNOVDLS) * Statistics.Variance(childrenFitnessNOVDLS));

            //Print statistics
            Console.WriteLine("P(NOVDSL), P(VDSL), C(NOVDLS), C(VDLS):");
            Console.WriteLine("AVG: " + Statistics.Mean(parentFitnessNOVDLS) + ", " + Statistics.Mean(parentFitnessVDLS) + ", " + Statistics.Mean(childrenFitnessNOVDLS) + ", " + MathNet.Numerics.Statistics.Statistics.Mean(childrenFitnessVDLS));
            Console.WriteLine("--------------------------------------");
            Console.WriteLine("Covariance:");          
            Console.WriteLine("P(NOVDSL) & C(NOVDSL): " + cov_pnovdsl_cnovdsl);      
            Console.WriteLine("P(VDSL) & C(VDSL): " + cov_pvdls_cvdls);

            Console.WriteLine("--------------------------------------");
            Console.WriteLine("Fitness correlation coefficient");
            Console.WriteLine("P(NOVDSL) & C(NOVDSL): " + Math.Round(nonc,3));
            Console.WriteLine("P(VDSL) & C(VDSL): " + Math.Round(pandc, 3));

            Console.WriteLine("--------------------------------------");
        }
}
}