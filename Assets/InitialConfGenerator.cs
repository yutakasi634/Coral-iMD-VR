﻿using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assertions;
using Nett;

public class InitialConfGenerator : MonoBehaviour
{
    public LennardJonesParticle       m_LJParticle;

    private float temperature = 300.0f;
    private float timescale   = 10.0f;
    private float kb          = 0.0019827f; // kcal/mol,  1 tau .=. 49 fs
    private float kb_scaled;
    private NormalizedRandom m_NormalizedRandom;

    private SystemManager              m_SystemManager;
    private UnderdampedLangevinManager m_UnderdampedLangevinManager;
    private HarmonicBondManager        m_HarmonicBondManager;

    private void Awake()
    {
        // initialize member variables
        kb_scaled = kb * timescale * timescale;
    }
    // Start is called before the first frame update
    void Start()
    {
        // read input file
        string input_file_path = Application.dataPath + "/../input/input.toml";
        TomlTable root = Toml.ReadFile(input_file_path);

        // generate initial particle position, velocity and system temperature
        List<TomlTable> systems                = root.Get<List<TomlTable>>("systems");
        if (2 <= systems.Count)
        {
            throw new System.Exception($"There are {systems.Count} systems. the multiple systems case is not supported.");
        }
        List<LennardJonesParticle> ljparticles = new List<LennardJonesParticle>();
        float[] upper_boundary = new float[3];
        float[] lower_boundary = new float[3];
        m_NormalizedRandom     = new NormalizedRandom();
        foreach (TomlTable system in systems)
        {
            temperature = system.Get<TomlTable>("attributes").Get<float>("temperature");
            if (system.ContainsKey("boundary_shape"))
            {
                TomlTable boundary_shape = system.Get<TomlTable>("boundary_shape");
                upper_boundary = boundary_shape.Get<float[]>("upper");
                lower_boundary = boundary_shape.Get<float[]>("lower");
            }
            else
            {
                throw new System.Exception("There is no boundary_shape information. UnlimitedBoundary is not supported now.");
            }
            List<TomlTable> particles = system.Get<List<TomlTable>>("particles");
            foreach (TomlTable particle_info in particles)
            {
                // initialize particle position
                float[] position = particle_info.Get<float[]>("pos");
                LennardJonesParticle new_particle =
                    Instantiate(m_LJParticle,
                                new Vector3(position[0], position[1], position[2]),
                                transform.rotation);

                // initialize particle velocity
                Rigidbody new_rigid = new_particle.GetComponent<Rigidbody>();
                new_rigid.mass = particle_info.Get<float>("m");
                if (particle_info.ContainsKey("vel"))
                {
                    float[] velocity = particle_info.Get<float[]>("vel");
                    new_rigid.velocity = new Vector3(velocity[0], velocity[1], velocity[2]);
                }
                else
                {
                    float sigma = Mathf.Sqrt(kb * temperature / new_rigid.mass);
                    new_rigid.velocity = new Vector3(m_NormalizedRandom.Generate() * sigma,
                                                     m_NormalizedRandom.Generate() * sigma,
                                                     m_NormalizedRandom.Generate() * sigma);
                }
                ljparticles.Add(new_particle);
            }
        }
        Debug.Log("System initialization finished.");


        // read simulator information
        if (root.ContainsKey("simulator"))
        {
            TomlTable simulator = root.Get<TomlTable>("simulator");
            if (simulator.ContainsKey("integrator"))
            {
                TomlTable integrator = simulator.Get<TomlTable>("integrator");
                if (integrator.ContainsKey("type"))
                {
                    string integrator_type = integrator.Get<string>("type");
                    if (integrator_type == "UnderdampedLangevin")
                    {
                        if (integrator.ContainsKey("gammas"))
                        {
                            int ljparticles_num = ljparticles.Count;
                            List<TomlTable> gammas_tables = integrator.Get<List<TomlTable>>("gammas");
                            float[]         gammas        = new float[ljparticles.Count];
                            foreach (TomlTable gamma_table in gammas_tables)
                            {
                                // TODO: check dupulicate and lacking of declaration.
                                gammas[gamma_table.Get<int>("index")] = gamma_table.Get<float>("gamma");
                            }
                            m_UnderdampedLangevinManager = GetComponent<UnderdampedLangevinManager>();
                            m_UnderdampedLangevinManager.Init(kb_scaled, temperature, ljparticles, gammas, timescale);
                            Debug.Log("UnderdampedLangevinManager initialization finished.");
                        }
                        else
                        {
                            throw new System.Exception("When you use UnderdampedLangevin integrator, you must specify gammas for integrator.");
                        }
                    }
                }
            }
        }

        // read forcefields information
        List<TomlTable> ffs        = root.Get<List<TomlTable>>("forcefields");
        float           max_radius = 0.0f;
        foreach (TomlTable ff in ffs)
        {
            if (ff.ContainsKey("local"))
            {
                List<TomlTable> local_ffs = ff.Get<List<TomlTable>>("local");
                foreach (TomlTable local_ff in local_ffs)
                {
                    string potential = local_ff.Get<string>("potential");
                    if (potential == "Harmonic")
                    {
                        var parameters = local_ff.Get<List<TomlTable>>("parameters");
                        var v0s = new List<float>();
                        var ks = new List<float>();
                        var ljrigid_pairs = new List<List<Rigidbody>>();
                        foreach (TomlTable parameter in parameters)
                        {
                            List<int> indices = parameter.Get<List<int>>("indices");
                            var ljrigid1 = ljparticles[indices[0]].GetComponent<Rigidbody>();
                            var ljrigid2 = ljparticles[indices[1]].GetComponent<Rigidbody>();
                            ljrigid_pairs.Add(new List<Rigidbody>() { ljrigid1, ljrigid2 });
                            v0s.Add(parameter.Get<float>("v0"));
                            ks.Add(parameter.Get<float>("k"));
                            Assert.AreEqual(indices.Count, 2,
                                "The length of indices must be 2.");
                        }
                        m_HarmonicBondManager = GetComponent<HarmonicBondManager>();
                        m_HarmonicBondManager.Init(v0s, ks, ljrigid_pairs, timescale);
                        Debug.Log("HarmonicBondManager initialization finished.");
                    }
                    else
                    {
                        throw new System.Exception($@"
                        Unknown local forcefields is specified. Available local forcefield is
                            - Harmonic");
                    }
                }
            }

            if (ff.ContainsKey("global"))
            {
                List<TomlTable> global_ffs = ff.Get<List<TomlTable>>("global");
                foreach (TomlTable global_ff in global_ffs)
                {
                    Assert.AreEqual("LennardJones", global_ff.Get<string>("potential"),
                        "The potential field is only allowed \"LennardJones\". Other potential or null is here.");
                    List<TomlTable> parameters = global_ff.Get<List<TomlTable>>("parameters");
                    foreach (TomlTable parameter in parameters)
                    {
                        int index = parameter.Get<int>("index");
                        float sigma = parameter.Get<float>("sigma"); // sigma correspond to diameter.
                        float radius = sigma / 2;
                        if (max_radius < radius)
                        {
                            max_radius = radius;
                        }
                        ljparticles[index].sphere_radius = radius;
                        ljparticles[index].epsilon = parameter.Get<float>("epsilon");
                        ljparticles[index].transform.localScale = new Vector3(sigma, sigma, sigma);
                    }
                }
            }
        }

        // Initialize SystemManager
        m_SystemManager = GetComponent<SystemManager>();
        m_SystemManager.Init(ljparticles, upper_boundary, lower_boundary, timescale);
        Debug.Log("SystemManager initialization finished.");

        // Set floor position
        GameObject floor = GameObject.Find("Floor");
        floor.transform.position = new Vector3(0.0f, lower_boundary[1] - max_radius, 0.0f);
    }
}
