# UnityOcclusionPerTriangleGPU
Code for the article: https://www.alexisbacot.com/blog/per-triangle-occlusion-on-the-gpu-in-1ms-with-unity

This should work with most version of Unity 2017+

Shader model 5.0 required to run the compute shader

The game view will show you the render texture used by the algorithm
The scene view will show you the visible triangle using gizmos

Move the occlusion camera around the scene (in the editor scene view) to refresh the visibility check

![screenshot](https://github.com/AlexisBacot/UnityOcclusionPerTriangleGPU/blob/main/OccSample_Room1.jpg?raw=true)
![screenshot](https://github.com/AlexisBacot/UnityOcclusionPerTriangleGPU/blob/main/OccSample_Far.jpg?raw=true)
