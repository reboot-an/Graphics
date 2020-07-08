
from .editor_priming import Editor_PrimingJob
from .editor_pinning_merge import Editor_PinningMergeJob
from .editor_pinning_update import Editor_PinningUpdateJob
from ..shared.namer import editor_filepath, editor_pinning_filepath

def create_editor_yml(metafile):

    yml_files = {}

    # editor priming jobs
    yml = {}
    for platform in metafile["platforms"]:
        for editor in metafile['editors']:
            job = Editor_PrimingJob(platform, editor, metafile["agent"])
            yml[job.job_id] = job.yml

    yml_files[editor_filepath()] = yml


    # editor pinning jobs
    yml = {}
    job = Editor_PinningUpdateJob(metafile["editor_pin_agent"], metafile["editor_pin_target_branch"], metafile["editor_pin_ci_branch"])
    yml[job.job_id] = job.yml

    job = Editor_PinningMergeJob(metafile["editor_pin_track"], metafile["editor_pin_agent"], metafile["editor_pin_target_branch"], metafile["editor_pin_ci_branch"])
    yml[job.job_id] = job.yml 

    yml_files[editor_pinning_filepath()] = yml


    return yml_files